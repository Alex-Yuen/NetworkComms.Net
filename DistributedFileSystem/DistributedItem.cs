﻿//  Copyright 2011 Marc Fletcher, Matthew Dean
//
//  This program is free software: you can redistribute it and/or modify
//  it under the terms of the GNU General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  This program is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU General Public License for more details.
//
//  You should have received a copy of the GNU General Public License
//  along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using NetworkCommsDotNet;
using DPSBase;

namespace DistributedFileSystem
{
    public class DistributedItem
    {
        public string ItemTypeStr { get; private set; }
        public long ItemCheckSum { get; private set; }
        public byte TotalNumChunks { get; private set; }
        public int ChunkSizeInBytes { get; private set; }

        //byte[] ItemBytes { get; set; }
        /// <summary>
        /// Originally we stored a single array but this creates considerable inefficienies when redistributing the data
        /// We have now moved to keeping the data stored as seperate chunks
        /// </summary>
        byte[][] ItemByteArray { get; set; }
        public int ItemBytesLength { get; private set; }

        public int ItemBuildCascadeDepth { get; private set; }

        public DateTime ItemBuildCompleted { get; private set; }

        /// <summary>
        /// Contains a record of which peers have which chunks of this file
        /// </summary>
        public SwarmChunkAvailability SwarmChunkAvailability { get; private set; }

        /// <summary>
        /// Key is chunkIndex and value is the request made
        /// </summary>
        Dictionary<byte, ChunkAvailabilityRequest> itemBuildTrackerDict;

        AutoResetEvent itemBuildWait;
        ManualResetEvent itemBuildComplete;

        /// <summary>
        /// Tracks which chunks are currently being provided to other peers
        /// </summary>
        //private int CurrentChunkEnterCounter { get; set; }
        object itemLocker = new object();

        public int TotalChunkSupplyCount { get; private set; }
        public int PushCount { get; private set; }

        List<string> assembleLog;
        object assembleLogLocker = new object();

        public DistributedItem(string itemTypeStr, byte[] itemBytes, ConnectionInfo seedConnectionInfo, int itemBuildCascadeDepth = 1)
        {
            Constructor(itemTypeStr, itemBytes, seedConnectionInfo, itemBuildCascadeDepth);
        }

        private void Constructor(string itemTypeStr, byte[] itemBytes, ConnectionInfo seedConnectionInfo, int itemBuildCascadeDepth)
        {
            //CurrentChunkEnterCounter = 0;
            TotalChunkSupplyCount = 0;
            PushCount = 0;

            //The md5 is based on not just the bytes but also the other parameters
            //using (MD5 md5 = new MD5CryptoServiceProvider())
            //    ItemCheckSum = BitConverter.ToString(md5.ComputeHash(itemBytes)).Replace("-", "");
            ItemCheckSum = Adler32.GenerateCheckSum(itemBytes);
            ItemBytesLength = itemBytes.Length;
            this.ItemBuildCascadeDepth = itemBuildCascadeDepth;

            this.ItemBuildCompleted = DateTime.Now;

            //Calculate the exactChunkSize if we split everything up into 255 pieces
            double exactChunkSize = (double)itemBytes.Length / 255.0;

            //If the item is too small we just use the minimumChunkSize
            //If we need something larger than MinChunkSizeInBytes we select appropriately
            this.ChunkSizeInBytes = (exactChunkSize <= DFS.MinChunkSizeInBytes ? DFS.MinChunkSizeInBytes : (int)Math.Ceiling(exactChunkSize));

            this.TotalNumChunks = (byte)(Math.Ceiling((double)itemBytes.Length / (double)ChunkSizeInBytes));

            //this.ItemBytes = itemBytes;
            //Break the itemBytes into chunks
            this.ItemByteArray = new byte[TotalNumChunks][];
            for (int i = 0; i < TotalNumChunks; i++)
            {
                byte[] currentChunkArray = new byte[(i == TotalNumChunks - 1 ? itemBytes.Length - (i * ChunkSizeInBytes) : ChunkSizeInBytes)];
                Buffer.BlockCopy(itemBytes, i * ChunkSizeInBytes, currentChunkArray, 0, currentChunkArray.Length);
                ItemByteArray[i] = currentChunkArray;
            }

            //Intialise the swarm availability
            SwarmChunkAvailability = new SwarmChunkAvailability(seedConnectionInfo, TotalNumChunks);

            if (DFS.loggingEnabled) DFS.logger.Debug("... created new original DFS item (" + this.ItemCheckSum + ").");
        }

        public DistributedItem(ItemAssemblyConfig assemblyConfig)
        {
            this.TotalChunkSupplyCount = 0;
            this.PushCount = 0;

            this.TotalNumChunks = assemblyConfig.TotalNumChunks;
            this.ChunkSizeInBytes = assemblyConfig.ChunkSizeInBytes;
            this.ItemCheckSum = assemblyConfig.ItemCheckSum;
            this.ItemBytesLength = assemblyConfig.TotalItemSizeInBytes;
            this.ItemBuildCascadeDepth = assemblyConfig.ItemBuildCascadeDepth;

            //this.ItemBytes = new byte[assemblyConfig.TotalItemSizeInBytes];
            this.ItemByteArray = new byte[TotalNumChunks][];

            //this.SwarmChunkAvailability = NetworkComms.DefaultSerializer.DeserialiseDataObject<SwarmChunkAvailability>(assemblyConfig.SwarmChunkAvailabilityBytes, NetworkComms.DefaultCompressor);
            this.SwarmChunkAvailability = DPSManager.GetDataSerializer<ProtobufSerializer>().DeserialiseDataObject<SwarmChunkAvailability>(assemblyConfig.SwarmChunkAvailabilityBytes);

            //As requests are made they are added to the build dict. We never remove a completed request.
            this.itemBuildTrackerDict = new Dictionary<byte, ChunkAvailabilityRequest>();

            this.itemBuildWait = new AutoResetEvent(false);
            this.itemBuildComplete = new ManualResetEvent(false);

            //Make sure that the original source added this node to the swarm before providing the assemblyConfig
            if (!SwarmChunkAvailability.PeerExistsInSwarm(NetworkComms.NetworkNodeIdentifier))
                throw new Exception("The current local node should have been added by the source.");

            //Bug fix incase we have just gotten the same file twice and the super node did not know that we dropped it
            //If the SwarmChunkAvailability thinks we have everything but our local version is not correct then clear our flags which will force rebuild
            if (SwarmChunkAvailability.PeerIsComplete(NetworkComms.NetworkNodeIdentifier, TotalNumChunks) && !LocalItemValid())
            {
                SwarmChunkAvailability.ClearAllLocalAvailabilityFlags();
                SwarmChunkAvailability.BroadcastLocalAvailability(ItemCheckSum);
            }

            if (DFS.loggingEnabled) DFS.logger.Debug("... created new DFS item from assembly config (" + this.ItemCheckSum + ").");
        }

        public void IncrementPushCount()
        {
            lock (itemLocker) PushCount++;
        }

        private void AddBuildLogLine(string newLine)
        {
            lock (assembleLogLocker)
            {
                if (assembleLog == null) assembleLog = new List<string>();

                assembleLog.Add(DateTime.Now.Hour.ToString() + "." + DateTime.Now.Minute.ToString() + "." + DateTime.Now.Second.ToString() + "." + DateTime.Now.Millisecond.ToString() + " - " + newLine);
            }
        }

        public string[] BuildLog()
        {
            lock (assembleLogLocker)
            {
                if (assembleLog != null)
                    return assembleLog.ToArray();
                else
                    return new string[0];
            }
        }

        public void AssembleItem(int assembleTimeoutSecs)
        {
            if (DFS.loggingEnabled) DFS.logger.Debug("Started DFS item assemble (" + this.ItemCheckSum + ").");

            AddBuildLogLine("Started DFS item assemble (" + this.ItemCheckSum + ").");

            //Used to load balance
            Random randGen = new Random();
            DateTime assembleStartTime = DateTime.Now;

            //Contact all known peers and request an update
            SwarmChunkAvailability.UpdatePeerAvailability(ItemCheckSum, ItemBuildCascadeDepth);

            NetworkComms.ConnectionEstablishShutdownDelegate connectionShutdownDuringBuild = new NetworkComms.ConnectionEstablishShutdownDelegate((Connection connection) =>
            {
                //On a closed conneciton we make sure we have no outstanding requests with that client
                lock (itemLocker)
                {
                    //Console.WriteLine("Disconnected - Removing requests to peer "+ connectionId);
                    itemBuildTrackerDict = (from current in itemBuildTrackerDict where current.Value.PeerConnectionInfo.NetworkIdentifier != connection.ConnectionInfo.NetworkIdentifier select current).ToDictionary(dict => dict.Key, dict => dict.Value);
                    itemBuildWait.Set();
                }
            });

            NetworkComms.AppendGlobalConnectionCloseHandler(connectionShutdownDuringBuild);

            //Loop until the local file is complete
            while (!LocalItemComplete())
            {
                #region BuildItem
                //The requests we are going to make this loop
                Dictionary<ConnectionInfo, List<ChunkAvailabilityRequest>> newRequests = new Dictionary<ConnectionInfo, List<ChunkAvailabilityRequest>>();
                int newRequestCount = 0;

                ///////////////////////////////
                ///// ENTER ITEMLOCKER ////////
                ///////////////////////////////
                lock (itemLocker)
                {
                    //Get the list of all current possible peers and chunks
                    //We get all the information we are going to need from the current swarm cache in one go
                    Dictionary<ConnectionInfo, PeerAvailabilityInfo> nonLocalPeerAvailability;
                    Dictionary<byte, Dictionary<ConnectionInfo, PeerAvailabilityInfo>> nonLocalChunkExistence = SwarmChunkAvailability.CachedNonLocalChunkExistences(TotalNumChunks, out nonLocalPeerAvailability);

                    //We only go any further if we were given some data
                    //if were werent chances are we are actually done
                    if (nonLocalChunkExistence.Count > 0)
                    {
                        AddBuildLogLine(nonLocalChunkExistence.Count + " chunks required.");

                        //We will want to know how many unique peers we can potentially contact
                        int maxPeers = (from current in nonLocalChunkExistence select current.Value.Count(entry => !entry.Value.PeerBusy)).Max();

                        //We will want to know how many chunks we have left to get
                        int numChunksLeft = TotalNumChunks - itemBuildTrackerDict.Count;

                        //Get list of chunks we don't have and order by rarity, starting with the rarest first
                        List<byte> chunkRarity = (from current in nonLocalChunkExistence
                                                  orderby current.Value.Count, randGen.NextDouble()
                                                  select current.Key).ToList();

                        //Check for request timeouts
                        #region ChunkRequestTimeout
                        byte[] currentTrackerKeys = itemBuildTrackerDict.Keys.ToArray();
                        for (int i = 0; i < currentTrackerKeys.Length; i++)
                        {
                            if (!itemBuildTrackerDict[currentTrackerKeys[i]].RequestComplete && (DateTime.Now - itemBuildTrackerDict[currentTrackerKeys[i]].RequestCreationTime).TotalMilliseconds > DFS.ChunkRequestTimeoutMS)
                            {
                                if (DFS.loggingEnabled) DFS.logger.Trace(" ... removing " + itemBuildTrackerDict[currentTrackerKeys[i]].PeerConnectionInfo.NetworkIdentifier + " peer from AssembleItem due to potential timeout.");

                                AddBuildLogLine("Removing " + itemBuildTrackerDict[currentTrackerKeys[i]].PeerConnectionInfo.NetworkIdentifier + " from AssembleItem due to potential timeout.");

                                //Console.WriteLine("      Request Timeout {0} - Removing request for chunk {1} from {2}.", DateTime.Now.ToString("HH:mm:ss.fff"), currentTrackerKeys[i], itemBuildTrackerDict[currentTrackerKeys[i]].PeerConnectionInfo.NetworkIdentifier);
                                //If we had a timeout we will not try that peer again
                                SwarmChunkAvailability.RemovePeerFromSwarm(itemBuildTrackerDict[currentTrackerKeys[i]].PeerConnectionInfo.NetworkIdentifier);
                                itemBuildTrackerDict.Remove(currentTrackerKeys[i]);
                            }
                        }
                        #endregion

                        //We want an array of peer identifiers with whom we have outstanding requests
                        //In the first instance we will always go to other peers first
                        List<ChunkAvailabilityRequest> nonIncomingOutstandingRequests = (from current in itemBuildTrackerDict.Values
                                                                                         where !current.RequestIncoming
                                                                                         select current).ToList();

                        //We only consider making new requests if we are allowed to
                        if (nonIncomingOutstandingRequests.Count < DFS.NumTotalGlobalRequests)
                        {
                            //Step 1 - Go through each chunk we dont have, and have not yet requested,
                            //starting with the rarest, and attempt to make a new request.
                            #region Step1
                            for (byte i = 0; i < chunkRarity.Count; i++)
                            {
                                //Make sure the chunk does exist somewhere in the swarm
                                if (nonLocalChunkExistence[chunkRarity[i]].Count == 0)
                                    throw new Exception("Every distributed item must have atleast one source for every chunk. Something bad has most likely happened to the original super peer.");

                                //If we have not yet made a request for this chunk there will be no entry
                                if (!itemBuildTrackerDict.ContainsKey(chunkRarity[i]))
                                {
                                    //We have to do this inside the for loop as the result will change once we add new requests
                                    List<ShortGuid> currentRequestIdentifiers = (nonIncomingOutstandingRequests.Select(entry => entry.PeerConnectionInfo.NetworkIdentifier).Union(newRequests.Select(entry => entry.Value[0].PeerConnectionInfo.NetworkIdentifier))).ToList();

                                    //Determine if this chunk contains non super peers, if it does we will never contact the super peers (keeps load on super peers low)
                                    //We have non super peers if the number of peers who are not us and are not super peers is greater than 0
                                    bool containsNonSuperPeers = (nonLocalChunkExistence[chunkRarity[i]].Count(entry => entry.Key.NetworkIdentifier != NetworkComms.NetworkNodeIdentifier && !entry.Value.SuperPeer) > 0);

                                    //If over half the number of swarm peers are completed we will use them rather than uncompleted peers
                                    bool useCompletedPeers = (SwarmChunkAvailability.NumCompletePeersInSwarm(TotalNumChunks) >= SwarmChunkAvailability.NumPeersInSwarm() / 2.0);

                                    //We can now determine which peers we could contact for this chunk
                                    ConnectionInfo[] possibleChunkPeers = (from current in nonLocalChunkExistence[chunkRarity[i]]
                                                                           //We don't want to to contact busy peers
                                                                           where !current.Value.PeerBusy
                                                                           //If we have nonSuperPeers then we only include the non super peers
                                                                           where (containsNonSuperPeers ? !current.Value.SuperPeer : true)
                                                                           //We don't want a peer from whom we currently await a response
                                                                           where !currentRequestIdentifiers.Contains(current.Key.NetworkIdentifier)
                                                                           //See comments within /**/ below for ordering notes
                                                                           orderby
                                                                               (useCompletedPeers ? 0 : current.Value.PeerChunkFlags.NumCompletedChunks()) ascending,
                                                                               (useCompletedPeers ? current.Value.PeerChunkFlags.NumCompletedChunks() : 0) descending,
                                                                               randGen.NextDouble() ascending
                                                                           select current.Key).ToArray();

                                    /*
                                    *Comments on ordering the available peers in the above linq statement*
                                    We want to avoid overloading individual peers. If we always went to the peer with the most complete item
                                    there are situations (in particular if we have a single complete non super peer), where all 
                                    peers which are building and item will go to a single peer.
                                    Because of that we go to the peer with the least data in the fist instance and this should help load balance
                                    We also add a random sort at the end to make sure we always go for peers in a different order on a subsequent loop
                                
                                    10/4/12
                                    We are modifying this sorting when over half the swarm peers have already completed the item
                                    */

                                    //We can only make a request if there are available peers
                                    if (possibleChunkPeers.Length > 0)
                                    {
                                        //We can now add the new request to the build dictionaries
                                        ChunkAvailabilityRequest newChunkRequest = new ChunkAvailabilityRequest(ItemCheckSum, chunkRarity[i], possibleChunkPeers[0]);

                                        if (newRequests.ContainsKey(possibleChunkPeers[0]))
                                            throw new Exception("We should not be choosing a peer we have already choosen in step 1");
                                        else
                                            newRequests.Add(possibleChunkPeers[0], new List<ChunkAvailabilityRequest> { newChunkRequest });

                                        newRequestCount++;

                                        AddBuildLogLine("NewChunkRequest Idx:" + newChunkRequest.ChunkIndex + ", Target:" + newChunkRequest.PeerConnectionInfo.RemoteEndPoint.Address.ToString() + ":" + newChunkRequest.PeerConnectionInfo.RemoteEndPoint.Port + ", Id:" + newChunkRequest.PeerConnectionInfo.NetworkIdentifier);

                                        itemBuildTrackerDict.Add(chunkRarity[i], newChunkRequest);

                                        //Once we have added a new request we should check if we have enough
                                        if (newRequestCount >= maxPeers ||  //If we already have a number of new requests equal to the max number of peers
                                            nonIncomingOutstandingRequests.Count + newRequestCount >= maxPeers * DFS.NumConcurrentRequests || //If the total number of outstanding requests is greater than the total number of peers * our concurrency factor
                                            nonIncomingOutstandingRequests.Count + newRequestCount >= numChunksLeft || //If the total number of requests is equal the number of chunks left
                                            nonIncomingOutstandingRequests.Count + newRequestCount >= DFS.NumTotalGlobalRequests) //If the total number of requests is equal to the total requests
                                            break;
                                    }
                                }
                            }
                            #endregion Step1

                            //Step 2 - Now that we've been through all chunks and peers once we can make concurrent requests if we want to
                            #region Step2
                            //We start with a list of outstanding and new requests
                            List<ConnectionInfo> currentRequestConnectionInfo = (nonIncomingOutstandingRequests.Select(entry => entry.PeerConnectionInfo).Union(newRequests.Select(entry => entry.Value[0].PeerConnectionInfo))).ToList();

                            //Update the max peers
                            maxPeers = currentRequestConnectionInfo.Count;

                            int loopSafety = 0;
                            while (nonIncomingOutstandingRequests.Count + newRequestCount < maxPeers * DFS.NumConcurrentRequests && //If the total number of requests is less than the total number of peers * our concurrency factor
                                nonIncomingOutstandingRequests.Count + newRequestCount < numChunksLeft && //If the total number of requests is less than the number of chunks left
                                nonIncomingOutstandingRequests.Count + newRequestCount < DFS.NumTotalGlobalRequests //If the total number of requests is less than the total requests limit
                                )
                            {
                                if (loopSafety > 1000)
                                    throw new Exception("Loop safety triggered. outstandingRequests=" + nonIncomingOutstandingRequests.Count +
                                ". newRequestCount=" + newRequestCount +
                                ". maxPeers=" + maxPeers +
                                ". numChunksLeft=" + numChunksLeft +
                                ".");

                                //We shuffle the peer list so that we never go in the same order on successive loops
                                currentRequestConnectionInfo = ShuffleList.Shuffle(currentRequestConnectionInfo).ToList();
                                for (int i = 0; i < currentRequestConnectionInfo.Count; i++)
                                {
                                    //We want to check here and skip this peer if we already have enough requests
                                    //Or if the peer is marked as busy
                                    int outstandingRequestsFromCurrentPeer = nonIncomingOutstandingRequests.Count(entry => entry.PeerConnectionInfo == currentRequestConnectionInfo[i]);
                                    int newRequestsFromCurrentPeer = 0;
                                    if (newRequests.ContainsKey(currentRequestConnectionInfo[i]))
                                        newRequestsFromCurrentPeer = newRequests[currentRequestConnectionInfo[i]].Count;

                                    if (outstandingRequestsFromCurrentPeer + newRequestsFromCurrentPeer >= DFS.NumConcurrentRequests)
                                        continue;

                                    //Its possible we have pulled out a peer for whom we no longer have availability info for
                                    if (nonLocalPeerAvailability.ContainsKey(currentRequestConnectionInfo[i]) && !SwarmChunkAvailability.PeerBusy(currentRequestConnectionInfo[i].NetworkIdentifier))
                                    {
                                        //which chunks does this peer have that we could use?
                                        ChunkFlags peerAvailability = nonLocalPeerAvailability[currentRequestConnectionInfo[i]].PeerChunkFlags;

                                        //We still look in order of chunk rarity
                                        for (int j = 0; j < chunkRarity.Count; j++)
                                        {
                                            if (!itemBuildTrackerDict.ContainsKey(chunkRarity[j]) && //If we don't have an outstanding request
                                                peerAvailability.FlagSet(chunkRarity[j])) //If the selected peer has this chunk
                                            {
                                                //We can now add the new request to the build dictionaries
                                                ChunkAvailabilityRequest newChunkRequest = new ChunkAvailabilityRequest(ItemCheckSum, chunkRarity[j], currentRequestConnectionInfo[i]);

                                                AddBuildLogLine("NewChunkRequest Idx:" + newChunkRequest.ChunkIndex + ", Target:" + newChunkRequest.PeerConnectionInfo.RemoteEndPoint.Address.ToString() + ":" + newChunkRequest.PeerConnectionInfo.RemoteEndPoint.Port + ", Id:" + newChunkRequest.PeerConnectionInfo.NetworkIdentifier);

                                                if (newRequests.ContainsKey(currentRequestConnectionInfo[i]))
                                                    newRequests[currentRequestConnectionInfo[i]].Add(newChunkRequest);
                                                else
                                                    newRequests.Add(currentRequestConnectionInfo[i], new List<ChunkAvailabilityRequest> { newChunkRequest });

                                                newRequestCount++;

                                                itemBuildTrackerDict.Add(chunkRarity[j], newChunkRequest);

                                                //We only add one request per peer per loop
                                                break;
                                            }

                                            if (j == chunkRarity.Count - 1)
                                                //If we have made it here then this peer has no data we can use
                                                maxPeers--;
                                        }
                                    }
                                    else
                                        //If we have come across a peer whom we have zero availability for we reduce the maxPeers
                                        maxPeers--;

                                    //Once we have added a new request we should check if we have enough
                                    if (nonIncomingOutstandingRequests.Count + newRequestCount >= maxPeers * DFS.NumConcurrentRequests || //If the total number of outstanding requests is greater than the total number of peers * our concurrency factor
                                        nonIncomingOutstandingRequests.Count + newRequestCount >= numChunksLeft || //If the total number of requests is equal the number of chunks left
                                        nonIncomingOutstandingRequests.Count + newRequestCount >= DFS.NumTotalGlobalRequests) //If the total number of requests is equal to the total requests
                                        break;
                                }

                                loopSafety++;
                            }
                            #endregion Step2
                        }
                    }
                }
                ///////////////////////////////
                ///// LEAVE ITEMLOCKER ////////
                ///////////////////////////////

                #region PerformChunkRequests
                //Send requests to each of the peers we have added to the contact list
                if (newRequests.Count > 0)
                {
                    foreach (KeyValuePair<ConnectionInfo, List<ChunkAvailabilityRequest>> outerRequest in newRequests)
                    {
                        //Create a copy so that it is thread safe
                        KeyValuePair<ConnectionInfo, List<ChunkAvailabilityRequest>> request = outerRequest;

                        //We can contact every peer seperately so that no single peer can hold up the build
                        Action requestAction = new Action(() =>
                        {
                            try
                            {
                                if (request.Value.Count > DFS.NumConcurrentRequests)
                                    throw new Exception("Number of requests, " + request.Value.Count + ", for client, " + request.Key.NetworkIdentifier + ", exceeds the maximum, " + DFS.NumConcurrentRequests + ".");

                                for (int i = 0; i < request.Value.Count; i++)
                                {
                                    #region RequestChunkFromPeer
                                    //Console.WriteLine("({0}) requesting chunk {1} from {2}.", DateTime.Now.ToString("HH:mm:ss.fff"), request.Value[i].ChunkIndex, request.Key.NetworkIdentifier);

                                    //We may already have an existing connection to the peer
                                    if (NetworkComms.ConnectionExists(request.Key.NetworkIdentifier, ConnectionType.TCP))
                                        NetworkComms.SendObject("DFS_ChunkAvailabilityInterestRequest", request.Key.NetworkIdentifier, false, request.Value[i]);
                                    else
                                    {
                                        ShortGuid establishedIdentifier = ShortGuid.Empty;
                                        NetworkComms.SendObject("DFS_ChunkAvailabilityInterestRequest", request.Key.RemoteEndPoint.Address.ToString(), request.Key.RemoteEndPoint.Port, false, request.Value[i], ref establishedIdentifier);

                                        //We can double check here that the ip address we have just succesfully connected to is still the same peer as in the swarm info
                                        if (establishedIdentifier != request.Key.NetworkIdentifier)
                                        {
                                            //If not we have no idea what chunks the new peer might have
                                            //Start by removing the old peer
                                            SwarmChunkAvailability.RemovePeerFromSwarm(request.Key.NetworkIdentifier);
                                            //Request an availability update from the one we just connected to
                                            //It's possible it will have sent one because of the DFS_ChunkAvailabilityInterestRequest but this makes double sure
                                            NetworkComms.SendObject("DFS_ChunkAvailabilityRequest", establishedIdentifier, false, ItemCheckSum);
                                        }
                                    }
                                    #endregion

                                    //Console.WriteLine("      ..({0}) chunk {1} requested from {2}.", DateTime.Now.ToString("HH:mm:ss.fff"), request.Value[i].ChunkIndex, request.Key.NetworkIdentifier);
                                }
                            }
                            catch (CommsException ex)
                            {
                                //If we can't connect to a peer we assume it's dead and don't try again
                                SwarmChunkAvailability.RemovePeerFromSwarm(request.Key.NetworkIdentifier);

                                //Console.WriteLine("CommsException {0} - Removing requests for peer " + request.Key.NetworkIdentifier, DateTime.Now.ToString("HH:mm:ss.fff"));
                                //NetworkComms.LogError(ex, "ChunkRequestError");

                                //On error remove the chunk requests
                                lock (itemLocker)
                                    itemBuildTrackerDict = (from current in itemBuildTrackerDict where !request.Value.Select(entry => entry.ChunkIndex).Contains(current.Key) select current).ToDictionary(dict => dict.Key, dict => dict.Value);

                                //Trigger a loop as there has been an error
                                itemBuildWait.Set();
                            }
                            catch (Exception ex)
                            {
                                NetworkComms.LogError(ex, "DFSAssembleItemError");
                                //Console.WriteLine("DFSAssembleItemError");

                                //On error remove the chunk requests
                                lock (itemLocker)
                                    itemBuildTrackerDict = (from current in itemBuildTrackerDict where !request.Value.Select(entry => entry.ChunkIndex).Contains(current.Key) select current).ToDictionary(dict => dict.Key, dict => dict.Value);

                                //Trigger a loop as there has been an error
                                itemBuildWait.Set();
                            }
                        });

                        Task.Factory.StartNew(requestAction);
                    }
                }

                #endregion

                AddBuildLogLine("Made " + newRequests.Count + " new chunk requests.");

                #endregion

                //Wait for incoming data, a complete build or a timeout.
                if (newRequests.Count > 0)
                {
                    //If we made requests we can wait with a longer timeout, incoming repies should trigger us out sooner if not
                    if (WaitHandle.WaitAny(new WaitHandle[] { itemBuildWait, itemBuildComplete }, 5000) == WaitHandle.WaitTimeout)
                    {
                        //NetworkComms.LogError(new Exception("Build wait timeout after 4secs. Item complete=" + SwarmChunkAvailability.PeerIsComplete(NetworkComms.NetworkNodeIdentifier, TotalNumChunks)), "AssembleWaitTimeout");
                        //Console.WriteLine("      Build wait timeout after 5secs, {0}. Item complete=" + SwarmChunkAvailability.PeerIsComplete(NetworkComms.NetworkNodeIdentifier, TotalNumChunks), DateTime.Now.ToString("HH:mm:ss.fff"));
                    }
                }
                else
                    //If we made no requests, then we have enough outstanding or all peers are busy
                    WaitHandle.WaitAny(new WaitHandle[] { itemBuildWait, itemBuildComplete }, DFS.PeerBusyTimeoutMS);

                if ((DateTime.Now - assembleStartTime).TotalSeconds > assembleTimeoutSecs)
                    throw new TimeoutException("AssembleItem() has taken longer than " + assembleTimeoutSecs + " secs so has been timed out.");

                if (DFS.DFSShutdownRequested)
                    return;
            }

            NetworkComms.RemoveGlobalConnectionCloseHandler(connectionShutdownDuringBuild);

            //Once we have a complete item we can broadcast our availability
            SwarmChunkAvailability.BroadcastLocalAvailability(ItemCheckSum);
            ItemBuildCompleted = DateTime.Now;

            //Close connections to other completed clients which are not a super peer
            //SwarmChunkAvailability.CloseConnectionToCompletedPeers(TotalNumChunks);

            if (DFS.loggingEnabled) DFS.logger.Debug(" ... completed DFS item assemble (" + this.ItemCheckSum + ") using " + SwarmChunkAvailability.NumPeersInSwarm() + " peers.");

            AddBuildLogLine("Completed assemble (" + this.ItemCheckSum + ") using " + SwarmChunkAvailability.NumPeersInSwarm() + " peers.");

            try { GC.Collect(); }
            catch (Exception) { }
        }

        public void HandleIncomingChunkReply(ChunkAvailabilityReply incomingReply, Connection connection)
        {
            try
            {
                if (incomingReply.ReplyState == ChunkReplyState.DataIncluded)
                    AddBuildLogLine("Incoming SUCCESS reply from " + connection + " chunk:" + incomingReply.ChunkIndex + " (" + incomingReply.ItemCheckSum + ")");
                else if (incomingReply.ReplyState == ChunkReplyState.ItemOrChunkNotAvailable)
                    AddBuildLogLine("Incoming FAILURE reply from " + connection + " chunk:" + incomingReply.ChunkIndex + " (" + incomingReply.ItemCheckSum + ")");
                else
                    AddBuildLogLine("Incoming BUSY reply from " + connection + " chunk:" + incomingReply.ChunkIndex + " (" + incomingReply.ItemCheckSum + ")");

                //We want to remove the chunk from the incoming build tracker so that it no longer counts as an outstanding request
                bool integrateChunk = false;
                lock (itemLocker)
                {
                    //If we still have an outstanding request for this chunk
                    if (itemBuildTrackerDict.ContainsKey(incomingReply.ChunkIndex))
                    {
                        //If data was included with the reply
                        if (incomingReply.ReplyState == ChunkReplyState.DataIncluded)
                        {
                            //If we are not currently handling a reply for the same chunk
                            if (!itemBuildTrackerDict[incomingReply.ChunkIndex].RequestIncoming)
                            {
                                itemBuildTrackerDict[incomingReply.ChunkIndex].RequestIncoming = true;
                                integrateChunk = true;
                            }
                        }
                        else
                        {
                            if (incomingReply.ReplyState == ChunkReplyState.ItemOrChunkNotAvailable)
                                //If no data was included it probably means our availability for this peer is wrong
                                //If we remove the peer here it prevents us from nailing it
                                //if the peer still has the file an availability update should be on it's way to us
                                SwarmChunkAvailability.RemovePeerFromSwarm(connection.ConnectionInfo.NetworkIdentifier);
                            else if (incomingReply.ReplyState == ChunkReplyState.PeerBusy)
                                SwarmChunkAvailability.SetPeerBusy(connection.ConnectionInfo.NetworkIdentifier);

                            //If no data was included, regardless of state, we need to remove the request and allow it to be recreated
                            if (!itemBuildTrackerDict[incomingReply.ChunkIndex].RequestIncoming)
                                itemBuildTrackerDict.Remove(incomingReply.ChunkIndex);
                        }
                    }
                    else
                    {
                        //We no longer have the requst for this reply, no worries we can still use it
                        //If the checksums match, it includes data and we don't already have it
                        if (ItemCheckSum == incomingReply.ItemCheckSum && incomingReply.ReplyState == ChunkReplyState.DataIncluded && !SwarmChunkAvailability.PeerHasChunk(NetworkComms.NetworkNodeIdentifier, incomingReply.ChunkIndex))
                        {
                            //We pretend we made the request already
                            ChunkAvailabilityRequest request = new ChunkAvailabilityRequest(ItemCheckSum, incomingReply.ChunkIndex, connection.ConnectionInfo);
                            request.RequestIncoming = true;
                            itemBuildTrackerDict.Add(incomingReply.ChunkIndex, request);

                            integrateChunk = true;
                        }
                    }
                }

                //As soon as we have done the above we should be making another request
                itemBuildWait.Set();

                if (integrateChunk)
                {
                    //Console.WriteLine(". {0} chunk {1} received.", DateTime.Now.ToString("HH:mm:ss.fff"), incomingReply.ChunkIndex);
                    #region StoreChunk
                    //We expect the final chunk to have a smaller length
                    if (incomingReply.ChunkData.Length != ChunkSizeInBytes && incomingReply.ChunkIndex < TotalNumChunks - 1)
                        throw new Exception("Provided bytes was " + incomingReply.ChunkData.Length + " bytes in length although " + ChunkSizeInBytes + " bytes were expected.");

                    if (incomingReply.ChunkIndex > TotalNumChunks)
                        throw new Exception("Provided chunkindex (" + incomingReply.ChunkIndex + ") is greater than the total num of the chunks for this item (" + TotalNumChunks + ").");

                    //Copy the received bytes into the results array
                    //Buffer.BlockCopy(incomingReply.ChunkData, 0, ItemBytes, incomingReply.ChunkIndex * ChunkSizeInBytes, incomingReply.ChunkData.Length);
                    this.ItemByteArray[incomingReply.ChunkIndex] = incomingReply.ChunkData;

                    //Record the chunk locally as available
                    SwarmChunkAvailability.RecordLocalChunkCompletion(incomingReply.ChunkIndex);

                    lock (itemLocker)
                    {
                        if (itemBuildTrackerDict.ContainsKey(incomingReply.ChunkIndex))
                        {
                            //Set both again incase the request was readded before we got here
                            itemBuildTrackerDict[incomingReply.ChunkIndex].RequestIncoming = true;
                            itemBuildTrackerDict[incomingReply.ChunkIndex].RequestComplete = true;
                        }
                        else
                        {
                            //We pretend we made the request already
                            ChunkAvailabilityRequest request = new ChunkAvailabilityRequest(ItemCheckSum, incomingReply.ChunkIndex, connection.ConnectionInfo);
                            request.RequestComplete = true;
                            request.RequestIncoming = true;
                            itemBuildTrackerDict.Add(incomingReply.ChunkIndex, request);
                        }
                    }

                    //If we have just completed the build we can set the build complete signal
                    if (LocalItemComplete())
                        itemBuildComplete.Set();

                    //We only broadcast our availability if the health metric of this chunk is less than
                    if (SwarmChunkAvailability.ChunkHealthMetric(incomingReply.ChunkIndex, TotalNumChunks) < 1)
                        SwarmChunkAvailability.BroadcastLocalAvailability(ItemCheckSum);

                    if (DFS.loggingEnabled) DFS.logger.Trace(" ... added data for chunk " + incomingReply.ChunkIndex + " to item " + ItemCheckSum + ".");
                    #endregion

                    //For the same reason we garbage collect at the server end when creating this data we want to 
                    //garbage collect after handling incoming data
                    try { GC.Collect(); }
                    catch (Exception) { }
                }
            }
            catch (Exception)
            {
                //We only remove a request if there was an error
                lock (itemLocker)
                    itemBuildTrackerDict.Remove(incomingReply.ChunkIndex);

                throw;
            }
        }

        public bool ChunkAvailableLocally(byte chunkIndex)
        {
            return SwarmChunkAvailability.PeerHasChunk(NetworkComms.NetworkNodeIdentifier, chunkIndex);
        }

        /// <summary>
        /// Returns the a copy of the bytes corresponding to the requested chunkIndex. Returns null is chunk is highly popular.
        /// </summary>
        /// <param name="chunkIndex"></param>
        /// <returns></returns>
        public byte[] GetChunkBytes(byte chunkIndex)
        {
            //If we have made it this far we are returning data
            if (SwarmChunkAvailability.PeerHasChunk(NetworkComms.NetworkNodeIdentifier, chunkIndex))
            {
                lock (itemLocker) TotalChunkSupplyCount++;

                return ItemByteArray[chunkIndex];
            }
            else
                throw new Exception("Attempted to acces DFS chunk which was not available locally");
        }

        /// <summary>
        /// Once the item has been fully assembled the completed bytes can be access via this method.
        /// </summary>
        /// <returns></returns>
        public byte[] AccessItemBytes()
        {
            if (LocalItemComplete())
            {
                if (LocalItemValid())
                {
                    byte[] returnArray = new byte[ItemBytesLength];
                    for (int i = 0; i < TotalNumChunks; i++)
                        Buffer.BlockCopy(ItemByteArray[i], 0, returnArray, i * ChunkSizeInBytes, ItemByteArray[i].Length);

                    return returnArray;
                }
                else
                {
                    ItemChunkCheckSums(true);
                    throw new Exception("Attempted to access item bytes but they are corrupted.");
                }
            }
            else
                throw new Exception("Attempted to access item bytes before all chunks had been retrieved.");
        }

        /// <summary>
        /// Returns true if itembytes validate correctly
        /// </summary>
        /// <returns></returns>
        public bool LocalItemValid()
        {
            if (ItemCheckSum == Adler32.GenerateCheckSum(ItemByteArray))
            //if (true)
            {
                return true;
            }
            else
                return false;
        }

        /// <summary>
        /// Returns the checksums for each chunk in the item
        /// </summary>
        /// <returns></returns>
        public long[] ItemChunkCheckSums(bool saveOut = false)
        {
            long[] returnValues = new long[TotalNumChunks];

            for (byte i = 0; i < TotalNumChunks; i++)
            {
                byte[] testBytes = GetChunkBytes(i);
                if (testBytes == null)
                    throw new Exception("Cant checksum a locked chunk.");
                else
                    returnValues[i] = Adler32.GenerateCheckSum(testBytes);

                //LeaveChunkBytes(i);
            }

            if (saveOut)
            {
#if iOS
                string fileName = "checkSumError " + DateTime.Now.Hour.ToString() + "." + DateTime.Now.Minute.ToString() + "." + DateTime.Now.Second.ToString() + "." + DateTime.Now.Millisecond.ToString() + " " + DateTime.Now.ToString("dd-MM-yyyy" + " [" + Thread.CurrentContext.ContextID + "]");
#else
                string fileName = "checkSumError " + DateTime.Now.Hour.ToString() + "." + DateTime.Now.Minute.ToString() + "." + DateTime.Now.Second.ToString() + "." + DateTime.Now.Millisecond.ToString() + " " + DateTime.Now.ToString("dd-MM-yyyy" + " [" + Process.GetCurrentProcess().Id + "-" + Thread.CurrentContext.ContextID + "]");
#endif
                using (System.IO.StreamWriter sw = new System.IO.StreamWriter(fileName + ".txt", false))
                {
                    for (int i = 0; i < returnValues.Length; i++)
                        sw.WriteLine(returnValues[i]);
                }
            }

            return returnValues;
        }

        /// <summary>
        /// Returns true once all chunks have been received and the itembytes has been validated
        /// </summary>
        /// <returns></returns>
        public bool LocalItemComplete()
        {
            lock (itemLocker)
                return SwarmChunkAvailability.PeerIsComplete(NetworkComms.NetworkNodeIdentifier, TotalNumChunks);
        }

        /// <summary>
        /// Triggers a client update of all clients in swarm. Returns within 50ms regardless of whether responses have yet been received.
        /// </summary>
        public void UpdateItemSwarmStatus(int responseTimeoutMS)
        {
            SwarmChunkAvailability.UpdatePeerAvailability(ItemCheckSum, 0, responseTimeoutMS);
        }
    }

    public class ShuffleList
    {
        static Random randomGen = new Random();
        public static IList<T> Shuffle<T>(IList<T> list)
        {
            IList<T> listCopy = list.ToList();
            int n = listCopy.Count;
            while (n > 1)
            {
                n--;
                int k = randomGen.Next(n + 1);
                T value = listCopy[k];
                listCopy[k] = listCopy[n];
                listCopy[n] = value;
            }

            return listCopy;
        }
    }
}
