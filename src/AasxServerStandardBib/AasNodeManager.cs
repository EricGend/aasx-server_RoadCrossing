/* ========================================================================
 * Copyright (c) 2005-2019 The OPC Foundation, Inc. All rights reserved.
 *
 * OPC Foundation MIT License 1.00
 * 
 * Permission is hereby granted, free of charge, to any person
 * obtaining a copy of this software and associated documentation
 * files (the "Software"), to deal in the Software without
 * restriction, including without limitation the rights to use,
 * copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the
 * Software is furnished to do so, subject to the following
 * conditions:
 * 
 * The above copyright notice and this permission notice shall be
 * included in all copies or substantial portions of the Software.
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
 * EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
 * OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
 * NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
 * HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
 * WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
 * OTHER DEALINGS IN THE SOFTWARE.
 *
 * The complete license agreement can be found here:
 * http://opcfoundation.org/License/MIT/1.00/
 * ======================================================================*/

using AdminShellNS;
using Extensions;
using Microsoft.IdentityModel.Tokens;
using MimeKit;
using Namotion.Reflection;
using Opc.Ua;
using Opc.Ua.Sample;
using Opc.Ua.Server;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Crypto.Tls;
using Org.BouncyCastle.Tls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Threading;
using System.Timers;
using System.Xml.Linq;
using System.Xml.XPath;
using static AasOpcUaServer.AasUaBaseEntity;
using static AasOpcUaServer.AasUaNodeHelper;

namespace AasOpcUaServer
{
    /// <summary>
    /// A node manager the diagnostic information exposed by the server.
    /// </summary>
    public class AasModeManager : SampleNodeManager
    {
        private AdminShellPackageEnv[] thePackageEnv = null;
        private AasxUaServerOptions theServerOptions = null;

        #region Constructors
        /// <summary>
        /// Initializes the node manager.
        /// </summary>
        public AasModeManager(
            Opc.Ua.Server.IServerInternal server,
            ApplicationConfiguration configuration,
            AdminShellPackageEnv[] env,
            AasxUaServerOptions serverOptions = null)
        :
            base(server)
        {
            thePackageEnv = env;
            theServerOptions = serverOptions;

            List<string> namespaceUris = new List<string>();
            namespaceUris.Add("http://opcfoundation.org/UA/i4aas/");
            namespaceUris.Add("http://admin-shell.io/samples/i4aas/instance/");
            // ReSharper disable once VirtualMemberCallInConstructor
            NamespaceUris = namespaceUris;

            m_typeNamespaceIndex = Server.NamespaceUris.GetIndexOrAppend(namespaceUris[0]);
            m_namespaceIndex = Server.NamespaceUris.GetIndexOrAppend(namespaceUris[1]);

            m_lastUsedId = 0;
        }
        #endregion

        #region INodeIdFactory Members
        /// <summary>
        /// Creates the NodeId for the specified node.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <param name="mode">Type or instance</param>
        /// <param name="node">The node.</param>
        /// <returns>The new NodeId.</returns>
        public NodeId New(ISystemContext context, AasUaBaseEntity.CreateMode mode, NodeState node)
        {
            if (mode == AasUaBaseEntity.CreateMode.Type)
            {
                uint id = Utils.IncrementIdentifier(ref m_lastUsedId);
                return new NodeId(id, m_typeNamespaceIndex);
            }
            else
            {
                uint id = Utils.IncrementIdentifier(ref m_lastUsedId);
                return new NodeId(id, m_namespaceIndex);
            }
        }
        #endregion

        public NodeId NewFromParent(ISystemContext context, AasUaBaseEntity.CreateMode mode, NodeState node, NodeState parent)
        {
            // create known node ids from the full path in the AAS
            // causes an exception if anything has more than one qualifier!
            if (parent == null)
            {
                return new NodeId(node.BrowseName.Name, m_namespaceIndex);
            }
            if (node.BrowseName.Name == "Qualifier")
            {
                return New(context, mode, node);
            }
            else
            {
                return new NodeId(parent.NodeId.Identifier.ToString() + "." + node.BrowseName.Name, m_namespaceIndex);
            }
        }

        public NodeId NewType(ISystemContext context, AasUaBaseEntity.CreateMode mode,
            NodeState node, uint preferredNumId = 0)
        {
            uint id = preferredNumId;
            if (id == 0)
                id = Utils.IncrementIdentifier(ref m_lastUsedTypeId);
            // this is thought to be a BUG in the OPCF code
            //// return new NodeId(preferredNumId, m_typeNamespaceIndex);
            if (mode == AasUaBaseEntity.CreateMode.Type)
                return new NodeId(id, m_typeNamespaceIndex);
            else
                return new NodeId(id, m_namespaceIndex);
        }

        public void SaveNodestateCollectionAsNodeSet2(ISystemContext context, NodeStateCollection nsc, Stream stream,
            bool filterSingleNodeIds)
        {
            Opc.Ua.Export.UANodeSet nodeSet = new Opc.Ua.Export.UANodeSet();
            nodeSet.LastModified = DateTime.UtcNow;
            nodeSet.LastModifiedSpecified = true;

            foreach (var n in nsc)
                nodeSet.Export(context, n);

            if (filterSingleNodeIds)
            {
                // MIHO: There might be DOUBLE nodeIds in the the set!!!!!!!!!! WTF!!!!!!!!!!!!!
                // Brutally eliminate them
                var nodup = new List<Opc.Ua.Export.UANode>();
                foreach (var it in nodeSet.Items)
                {
                    var found = false;
                    foreach (var it2 in nodup)
                        if (it.NodeId == it2.NodeId)
                            found = true;
                    if (found)
                        continue;
                    nodup.Add(it);
                }
                nodeSet.Items = nodup.ToArray();
            }

            nodeSet.Write(stream);
        }

        #region INodeManager Members
        /// <summary>
        /// Does any initialization required before the address space can be used.
        /// </summary>
        /// <remarks>
        /// The externalReferences is an out parameter that allows the node manager to link to nodes
        /// in other node managers. For example, the 'Objects' node is managed by the CoreNodeManager and
        /// should have a reference to the root folder node(s) exposed by this node manager.  
        /// </remarks>
        public override void CreateAddressSpace(IDictionary<NodeId, IList<Opc.Ua.IReference>> externalReferences)
        {
            lock (Lock)
            {
                base.CreateAddressSpace(externalReferences);

                // Note: might be helpful for debugging
                //// var env = new AdminShell.PackageEnv("Festo-USB-stick-sample-admin-shell.aasx");

                if (true)
                {
                    var builder = new AasEntityBuilder(this, thePackageEnv, null, this.theServerOptions);

                    // Root of whole structure is special, needs to link to external reference
                    builder.RootAAS = builder.CreateAddFolder(AasUaBaseEntity.CreateMode.Instance, null, "AASROOT");

                    var methodeFolder = builder.CreateAddFolder(AasUaBaseEntity.CreateMode.Instance, builder.RootAAS, "MethodesFolder");

                    Argument[] inputArgList = new Argument[1];
                    inputArgList[0] = new Argument();
                    inputArgList[0].Name = "RSUetsi";
                    inputArgList[0].Description = "new messages to add to server";
                    inputArgList[0].DataType = DataTypeIds.String;
                    inputArgList[0].ValueRank = ValueRanks.Scalar;

                    builder.CreateAddMethodState(methodeFolder, CreateMode.Instance, "Operation Instance",
                            referenceTypeFromParentId: ReferenceTypeIds.HasComponent,
                            onCalled: HandleRsuEtsiMessage,
                            inputArgs: inputArgList
                            //TBD.: send hole aas back to RSU outputArgs: outputArgList
                            );

                    //Under Construction


                    // Note: this is TOTALLY WEIRD, but it establishes an inverse reference .. somehow
                    this.AddExternalReferencePublic(new NodeId(85, 0), ReferenceTypeIds.Organizes, false,
                        builder.RootAAS.NodeId, externalReferences);
                    this.AddExternalReferencePublic(builder.RootAAS.NodeId, ReferenceTypeIds.Organizes, true,
                        new NodeId(85, 0), externalReferences);


                    // Folders for DataSpecs
                    // DO NOT USE THIS FEATURE -> Data Spec are "under" the CDs
                    //// builder.RootDataSpecifications = builder.CreateAddFolder(
                    //// builder.RootAAS, "DataSpecifications");
                    //// builder.RootDataSpecifications = builder.CreateAddObject(
                    //// builder.RootAAS, "DataSpecifications");

                    if (false)
                    // ReSharper disable once HeuristicUnreachableCode
#pragma warning disable 162
                    {
                        // Folders for Concept Descriptions
                        // ReSharper disable once HeuristicUnreachableCode
                        builder.RootConceptDescriptions = builder.CreateAddFolder(
                            AasUaBaseEntity.CreateMode.Instance,
                            builder.RootAAS, "ConceptDescriptions");

                        // create missing dictionary entries
                        builder.RootMissingDictionaryEntries = builder.CreateAddFolder(
                            AasUaBaseEntity.CreateMode.Instance,
                            builder.RootAAS, "DictionaryEntries");
                    }
#pragma warning restore 162
                    else
                    {
                        // create folder(s) under root
                        var topOfDict = builder.CreateAddObject(null,
                            AasOpcUaServer.AasUaBaseEntity.CreateMode.Instance, "Dictionaries",
                            referenceTypeFromParentId: null,
                            typeDefinitionId: builder.AasTypes.DictionaryFolderType.GetTypeNodeId());
                        // Note: this is TOTALLY WEIRD, but it establishes an inverse reference .. somehow
                        // 2253 = Objects?
                        this.AddExternalReferencePublic(new NodeId(2253, 0),
                            ReferenceTypeIds.HasComponent, false, topOfDict.NodeId, externalReferences);
                        this.AddExternalReferencePublic(topOfDict.NodeId,
                            ReferenceTypeIds.HasComponent, true, new NodeId(2253, 0), externalReferences);

                        // now, create a dictionary under ..
                        // Folders for Concept Descriptions
                        builder.RootConceptDescriptions = builder.CreateAddObject(topOfDict,
                            AasOpcUaServer.AasUaBaseEntity.CreateMode.Instance, "ConceptDescriptions",
                            referenceTypeFromParentId: ReferenceTypeIds.HasComponent,
                            typeDefinitionId: builder.AasTypes.DictionaryFolderType.GetTypeNodeId());

                        // create missing dictionary entries
                        builder.RootMissingDictionaryEntries = builder.CreateAddObject(topOfDict,
                            AasOpcUaServer.AasUaBaseEntity.CreateMode.Instance, "DictionaryEntries",
                            referenceTypeFromParentId: ReferenceTypeIds.HasComponent,
                            typeDefinitionId: builder.AasTypes.DictionaryFolderType.GetTypeNodeId());
                    }

                    // start process
                    foreach (var ee in thePackageEnv)
                    {
                        if (ee != null)
                            builder.CreateAddInstanceObjects(ee.AasEnv);
                    }
                }

                // Try: ensure the reverse refernces exist.
                //// AddReverseReferences(externalReferences);

                if (theServerOptions != null
                    && theServerOptions.SpecialJob == AasxUaServerOptions.JobType.ExportNodesetXml)
                {
                    try
                    {
                        // empty list
                        var nodesToExport = new NodeStateCollection();

                        // apply filter criteria
                        foreach (var y in this.PredefinedNodes)
                        {
                            var node = y.Value;
                            if (theServerOptions.ExportFilterNamespaceIndex != null
                                && !theServerOptions.ExportFilterNamespaceIndex.Contains(node.NodeId.NamespaceIndex))
                                continue;
                            nodesToExport.Add(node);
                        }

                        // export
                        Utils.Trace("Writing export file: " + theServerOptions.ExportFilename);
                        var stream = new StreamWriter(theServerOptions.ExportFilename);

                        //// nodesToExport.SaveAsNodeSet2(this.SystemContext, stream.BaseStream, null, 
                        //// theServerOptions != null && theServerOptions.FilterForSingleNodeIds);

                        //// nodesToExport.SaveAsNodeSet2(this.SystemContext, stream.BaseStream);
                        SaveNodestateCollectionAsNodeSet2(this.SystemContext, nodesToExport, stream.BaseStream,
                            theServerOptions != null && theServerOptions.FilterForSingleNodeIds);

                        try
                        {
                            stream.Close();
                        }
                        catch (Exception ex)
                        {
                            AdminShellNS.LogInternally.That.SilentlyIgnoredError(ex);
                        }

                        // stop afterwards
                        if (theServerOptions.FinalizeAction != null)
                        {
                            Utils.Trace("Requesting to shut down application..");
                            theServerOptions.FinalizeAction();
                        }

                    }
                    catch (Exception ex)
                    {
                        Utils.Trace(ex, "When exporting to {0}", "" + theServerOptions.ExportFilename);
                    }

                    // shutdown ..

                }

                Debug.WriteLine("Done creating custom address space!");
                Utils.Trace("Done creating custom address space!");
            }
        }

        //TODO: Das muss noch woandersd hin
        ISystemContext _context;
        private ServiceResult HandleRsuEtsiMessage(ISystemContext context, MethodState method, IList<object> inputArguments, IList<object> outputArguments)
        { //Todo: try catch errors
            _context = context;
            //Add To coresponding AAS
            string xmlString = "<root>" + inputArguments[0] + "</root>";
            string senderId = "";
            NodeId parentNode = new NodeId();
            NodeId instanceNode = new NodeId();
            NodeState messageNodeInServer = Find(parentNode);
            SubmodelElementCollection v2xMessageCollection = new SubmodelElementCollection();
            XElement messageFromRsu = XElement.Parse(xmlString);

            var builder = new AasEntityBuilder(this, thePackageEnv, null, this.theServerOptions);

            // Root of whole structure is special, needs to link to external reference
            //builder.RootAAS = builder.CreateAddFolder(AasUaBaseEntity.CreateMode.Instance, null, "AASROOT");
            try
            {
                foreach (XElement message in messageFromRsu.Elements())
                {
                    string v2xMessageName = message.Name.ToString();
                    if (v2xMessageName.Equals("DENM"))
                        senderId = message.Descendants("originatingStationID").FirstOrDefault().Value;
                    else
                        senderId = message.Descendants("stationID").FirstOrDefault().Value;

                    string instanceNodeString = "ns=3;s=AASROOT.RSU-nachrichtenzentriert.EnvironmentModel." + v2xMessageName + ".originalStationId = " + senderId;
                    messageNodeInServer = Find(new NodeId(instanceNodeString));

                    v2xMessageCollection = XmlToSubmodellcollectionParser(message);
                    v2xMessageCollection.IdShort = "originalStationId = " + senderId;
                    //add to local AAS


                    //add to OPC Model
                    string parentNodeName = "ns=3;s=AASROOT.RSU-nachrichtenzentriert.EnvironmentModel." + v2xMessageName;
                    parentNode = new NodeId(parentNodeName);
                    builder.AasTypes.SubmodelWrapper.CreateAddElements(Find(parentNode), CreateMode.Instance, v2xMessageCollection, modellingRule: ModellingRule.Mandatory);

                    //preparation to delete from Submodell later
                    switch (v2xMessageName)
                    {
                        case "CAM":
                        case "CPM":
                        case "VAM":
                        case "SREM":
                            TimerForV2xMessage(v2xMessageName, message.Descendants("stationID").FirstOrDefault().Value, 5000);
                            break;
                            
                        case "DENM":
                            var cancellationFlag = message.Descendants("termination").FirstOrDefault();
                            if (cancellationFlag == null)
                                break;
                            if (cancellationFlag.Value.Equals("1")) //value is only set when DENM is cancelled (0) or negated (1)
                            {
                                SubmodelElementCollection submodellElemet = (SubmodelElementCollection)builder.packages[0].AasEnv.Submodels.FirstOrDefault(x => x.IdShort.Equals("EnvironmentModel")).FindSubmodelElementByIdShort(v2xMessageName);
                                submodellElemet.Value.Remove(submodellElemet.Value.FirstOrDefault(x => x.IdShort.Equals("originalStationId = " + senderId)));
                                v2xMessageCollection = submodellElemet;
                                parentNodeName = "ns=3;s=AASROOT.RSU-nachrichtenzentriert.EnvironmentModel";
                                parentNode = new NodeId(parentNodeName);
                                builder.AasTypes.SubmodelWrapper.CreateAddElements(Find(parentNode), CreateMode.Instance, v2xMessageCollection, modellingRule: ModellingRule.Mandatory);

                                //TerminateV2Xmessage(v2xMessageName, message.Descendants("originatingStationID").FirstOrDefault().Value);
                            }
                            break;

                        case "IVIM":
                            string ivimStatus = message.Descendants("iviStatus").FirstOrDefault().Value;
                            if (!(ivimStatus.Equals("0") || ivimStatus.Equals("1"))) //value is only set when IVIM is cancelled (0) or negated (1)
                            {
                                break;
                            }
                            TerminateV2Xmessage(v2xMessageName, message.Descendants("stationID").FirstOrDefault().Value);
                            break;

                        case "SSEM":
                            string ssemStatus = message.Descendants("ssemStatus").FirstOrDefault().Value;
                            if (!(ssemStatus.Equals("0") || ssemStatus.Equals("1"))) //value is only set when SSEM is cancelled (0) or negated (1)
                            {
                                break;
                            }
                            TerminateV2Xmessage(v2xMessageName, message.Descendants("stationID").FirstOrDefault().Value);
                            break;
                    }
                }

                return ServiceResult.Good;
            }
            catch (Exception ex)
            {
                return new ServiceResult(0x80000000, ex.Message);

            }
        }
        //TODO; weiter hoch setzen
        private Dictionary<string, string, System.Timers.Timer> activeV2xMessages = new Dictionary<string, string, System.Timers.Timer>();

        private void TimerForV2xMessage(string messageType, string messageId, int timerInMills)
        {
            //keep message alive while constantly send
            if (activeV2xMessages.ContainsKey(messageType, messageId))
            {
                var timer = activeV2xMessages[Tuple.Create(messageType, messageId)];
                timer.Stop();
                timer.AutoReset = false;
                timer.Start();
                Console.WriteLine("The Timer event was reset at {0:HH:mm:ss.fff}",
                          DateTime.UtcNow);
            }
            //first appearance of message
            else
            {
                var timer = new System.Timers.Timer(timerInMills);
                activeV2xMessages.Add(messageType, messageId, timer);
                timer.Elapsed += (sender, e) => OnTimedEvent(sender, e, messageType, messageId);
                timer.Enabled = true;
                timer.AutoReset=false;
                Console.WriteLine("The Timer event was set at {0:HH:mm:ss.fff}",
                          DateTime.UtcNow);
                
            }
        }

        private void OnTimedEvent(Object source, ElapsedEventArgs e, string v2xMessageType, string originatorId)
        {
            activeV2xMessages.Remove(Tuple.Create(v2xMessageType, originatorId));
            TerminateV2Xmessage(v2xMessageType, originatorId);
            Console.WriteLine("The Elapsed event was raised at {0:HH:mm:ss.fff}",
                              e.SignalTime);
        }
        private void TerminateV2Xmessage(string v2xMessageType, string originatorId)
        {
            string instanceNodeString = "ns=3;s=AASROOT.RSU-nachrichtenzentriert.EnvironmentModel." + v2xMessageType + ".originalStationId = " + originatorId;
            DeleteNode((ServerSystemContext) _context, new NodeId(instanceNodeString));
            //TODO: Bei CAM wird beim updaten der timer der browsename nicht verändert, keine ahnung warum.
        }


        private SubmodelElementCollection XmlToSubmodellcollectionParser(XElement message)
        {
            var collection = new SubmodelElementCollection();

            message.Name = "root"; //identify if something is diretcly under the root on every v2x message
            foreach (var element in message.Descendants())
            {
                if (element.Parent.Name.ToString() == "root")
                {
                    collection.AddChild(new SubmodelElementCollection(idShort: element.Name.ToString()));
                }
                else
                {
                    SubmodelElementCollection parent = FindParentByIdShort(collection, element.Parent.Name.ToString());

                    if (element.HasElements) //Submodellcollections
                    {
                        parent.AddChild(new SubmodelElementCollection(idShort: element.Name.ToString()));
                    }
                    else
                    {
                        if (element.Value.IsNullOrEmpty())  //Special items like ecoDrive: <type> <ecoDrive/> </type >
                        {
                            parent.AddChild(new Property(DataTypeDefXsd.String,
                                                    idShort: element.Name.ToString(),
                                                    value: element.Name.ToString()
                                                    ));
                        }
                        else //normal Properties
                        {
                            parent.AddChild(new Property(DataTypeDefXsd.String,
                                                    idShort: element.Name.ToString(),
                                                    value: element.Value.ToString()
                                                    ));
                        }
                    }
                }
            }
            return collection;
        }

        private SubmodelElementCollection FindParentByIdShort(SubmodelElementCollection collection, string idShort)
        {
            foreach (var element in collection.Descend().OfType<SubmodelElementCollection>())
            {
                if (element.IdShort == idShort)
                {
                    return element;
                }
                else
                {
                    FindParentByIdShort(element, idShort);
                }
            }
            return null;
        }

        public NodeStateCollection GenerateInjectNodeStates()
        {
            // new list
            var res = new NodeStateCollection();

            // Missing Object Types
            res.Add(AasUaNodeHelper.CreateObjectType("BaseInterfaceType",
                ObjectTypeIds.BaseObjectType, new NodeId(17602, 0)));
            res.Add(AasUaNodeHelper.CreateObjectType("DictionaryFolderType",
                ObjectTypeIds.FolderType, new NodeId(17591, 0)));
            res.Add(AasUaNodeHelper.CreateObjectType("DictionaryEntryType",
                ObjectTypeIds.BaseObjectType, new NodeId(17589, 0)));
            res.Add(AasUaNodeHelper.CreateObjectType("UriDictionaryEntryType",
                new NodeId(17589, 0), new NodeId(17600, 0)));
            res.Add(AasUaNodeHelper.CreateObjectType("IrdiDictionaryEntryType",
                new NodeId(17589, 0), new NodeId(17598, 0)));

            // Missing Reference Types
            res.Add(AasUaNodeHelper.CreateReferenceType("HasDictionaryEntry", "DictionaryEntryOf",
                ReferenceTypeIds.NonHierarchicalReferences, new NodeId(17597, 0)));
            res.Add(AasUaNodeHelper.CreateReferenceType("HasInterface", "InterfaceOf",
                ReferenceTypeIds.NonHierarchicalReferences, new NodeId(17603, 0)));
            res.Add(AasUaNodeHelper.CreateReferenceType("HasAddIn", "AddInOf",
                ReferenceTypeIds.HasComponent, new NodeId(17604, 0)));

            // deliver list
            return res;
        }

        public void AddReference(NodeId node, Opc.Ua.IReference reference)
        {
            var dict = new Dictionary<NodeId, IList<Opc.Ua.IReference>>();
            // ReSharper disable once RedundantExplicitArrayCreation
            dict.Add(node, new List<Opc.Ua.IReference>(new Opc.Ua.IReference[] { reference }));
            this.AddReferences(dict);
        }

        /// <summary>
        /// Loads a node set from a file or resource and addes them to the set of predefined nodes.
        /// </summary>
        protected override NodeStateCollection LoadPredefinedNodes(ISystemContext context)
        {
            NodeStateCollection predefinedNodes = new NodeStateCollection();
            return predefinedNodes;
        }

        /// <summary>
        /// Replaces the generic node with a node specific to the model.
        /// </summary>
        protected override NodeState AddBehaviourToPredefinedNode(ISystemContext context, NodeState predefinedNode)
        {
            return predefinedNode;
        }

        /// <summary>
        /// Does any processing after a monitored item is created.
        /// </summary>
        protected override void OnCreateMonitoredItem(
            ISystemContext systemContext,
            MonitoredItemCreateRequest itemToCreate,
            MonitoredNode monitoredNode,
            DataChangeMonitoredItem monitoredItem)
        {
            // TBD
        }

        /// <summary>
        /// Does any processing after a monitored item is created.
        /// </summary>
        protected override void OnModifyMonitoredItem(
            ISystemContext systemContext,
            MonitoredItemModifyRequest itemToModify,
            MonitoredNode monitoredNode,
            DataChangeMonitoredItem monitoredItem,
            double previousSamplingInterval)
        {
            // TBD
        }

        /// <summary>
        /// Does any processing after a monitored item is deleted.
        /// </summary>
        protected override void OnDeleteMonitoredItem(
            ISystemContext systemContext,
            MonitoredNode monitoredNode,
            DataChangeMonitoredItem monitoredItem)
        {
            // TBD
        }

        /// <summary>
        /// Does any processing after a monitored item is created.
        /// </summary>
        protected override void OnSetMonitoringMode(
            ISystemContext systemContext,
            MonitoredNode monitoredNode,
            DataChangeMonitoredItem monitoredItem,
            MonitoringMode previousMode,
            MonitoringMode currentMode)
        {
            // TBD
        }
        #endregion

        #region Private Fields
        private ushort m_namespaceIndex;
        private ushort m_typeNamespaceIndex;
        private long m_lastUsedId;
        private long m_lastUsedTypeId;
        #endregion
    }
    public class Dictionary<TKey1, TKey2, TValue> : Dictionary<Tuple<TKey1, TKey2>, TValue>, IDictionary<Tuple<TKey1, TKey2>, TValue>
    {

        public TValue this[TKey1 key1, TKey2 key2]
        {
            get { return base[Tuple.Create(key1, key2)]; }
            set { base[Tuple.Create(key1, key2)] = value; }
        }

        public void Add(TKey1 key1, TKey2 key2, TValue value)
        {
            base.Add(Tuple.Create(key1, key2), value);
        }

        public bool ContainsKey(TKey1 key1, TKey2 key2)
        {
            return base.ContainsKey(Tuple.Create(key1, key2));
        }
    }
   
}
