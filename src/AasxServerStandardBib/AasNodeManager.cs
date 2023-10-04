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
using Opc.Ua;
using Opc.Ua.Sample;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using static AasOpcUaServer.AasUaBaseEntity;

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

                    //TODO: under construction
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

        private ServiceResult HandleRsuEtsiMessage(ISystemContext context, MethodState method, IList<object> inputArguments, IList<object> outputArguments)
        {
            //checkmate
            //TODO: Add To coresponding AAS
            string xmlString = "<root>" + inputArguments[0] + "</root>";
            XElement messageFromRsu = XElement.Parse(xmlString);

            var builder = new AasEntityBuilder(this, thePackageEnv, null, this.theServerOptions);

            // Root of whole structure is special, needs to link to external reference
            builder.RootAAS = builder.CreateAddFolder(AasUaBaseEntity.CreateMode.Instance, null, "AASROOT");

            foreach (XElement message in messageFromRsu.Elements())
            {
                switch (message.Name.ToString())
                {
                    case "CAM":

                        //managing all CAMs and moving them to historical data after 1sec - new CAM should be available

                        // Erstellen eines neuen Object Nodes + Subnodes für Informationen konform der Verwaltungsschale

                        //1s Timer für Node erstellen

                        //Nach ablauf des Timers node löschen und in DB Historical Data speichern

                        break;
                    case "DENM":
                        var originateId = builder.LookupNodeRecordFromIdentification("AASROOT.RSU.DENM.denm.management.actionID.originatingStationID");
                        if (originateId != null )   
                            break;
                        


                        //Adding DENM when no other DENM with same ActionId is currentlx active //Was wenn denm inaktiv erklärt wird und zirkulärer bezug wieder auf active setzt?
                        //Rermove when Cancallation DENM with same Action ID or Negation DENM // Was wenn fahrzeug abgeschleppt werden muss und DENM nicht aufgehoben wird?
                        // Kann ic fahrzeug direklt anfunken ob es noch in reichweite ist?

                        //je nach Inhalt :neuen Node fürm DENM erstellen //Node updaten // Node löschen // IMMER in historical Data aufnehmen 

                        //wie mit aktiven DENMs umgehen, die nicht gelöscht wurden?


                        break;
                    case "MAPEM":

                        //immer zusammmen mit spatem übertragen
                        //sollte sich über die zeit wenig verändern 
                        // ==> Prüfen ob Änderung besteht in Client um Load auf Server zu verringern?

                        //MAPEM auf Änderungen prüfenj, bei Änderung VWS und historical Data updaten 

                        break;
                    case "SPATEM":
                        // wird bei jedem TLM (Traffic Light maneuver zusammen mit MAPEM übertragen (also je ampelphase?)

                        //immer VWS und historical data updaten

                        break;
                    case "IVIM":
                        //Infrastructure to Vehoicle (bspw.: Straßenschilder, Bauarbeiten an Straßen, ...)
                        //recht variable

                        //Vorgehen wie bei MAPEM  


                        break;
                    case "CPM":
                        //bereitstellung von Sensordaten andewrer Teilnehmer zum besseren Überblick
                        //hochdynamisch 
                        //wie umgehen mit verscheidenen Sensordaten unterschiedlicher Teilnehmer von einem anderen Teilnehmer?

                        //Hioer müssen sensordaten zusammengefügt werden ... keine Ahnung, erstmalk außenm vor lassen


                        break;
                    case "SSEM":
                        //Bestätigung des SREM 

                        //SSEM eintrag anlegenin VWS
                        //SSEM updaten
                        //SSEM lösche 
                        break;
                    case "SREM":
                        //Anfragen von Ampel priorisierung (ÖPNV) oder Schaltung von Ampeln durch Sicherheitsdienste (Krankenwagen)
                        //Hochdynamisch

                        //SREM eintrag anlegenin VWS
                        //SREM updaten
                        //SREM lösche 

                        break;
                    case "RTCMEM":
                        //Positionierungskorrektur, bereitgestellt von STraßenequipment zur korrektur von mobilenen einheiten 
                        //Hochdynamisch

                        //eigentlich nur für historical data interessabt, evtl updaten des Modelles auf
                        //Grundlagen von anderen Sensordaten, da Vehicle sonst mehrfach existiert?
                        break;

                }
            }
            return ServiceResult.Good;
            throw new Exception("no Good Status");
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
}
