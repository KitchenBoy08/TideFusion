﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using LabFusion.Data;
using LabFusion.Representation;
using LabFusion.Utilities;
using LabFusion.Grabbables;
using LabFusion.Syncables;
using LabFusion.Patching;

using SLZ;
using SLZ.Interaction;
using SLZ.Props.Weapons;

namespace LabFusion.Network
{
    [Net.DelayWhileTargetLoading]
    public class PuppetMasterKillMessage : FusionMessageHandler
    {
        public override byte? Tag => NativeMessageTag.PuppetMasterKill;

        public override void HandleMessage(byte[] bytes, bool isServerHandled = false)
        {
            using FusionReader reader = FusionReader.Create(bytes);
            var data = reader.ReadFusionSerializable<PropReferenceData>();
            // Send message to other clients if server
            if (NetworkInfo.IsServer && isServerHandled)
            {
                using var message = FusionMessage.Create(Tag.Value, bytes);
                MessageSender.BroadcastMessageExcept(data.smallId, NetworkChannel.Reliable, message, false);
            }
            else
            {
                if (SyncManager.TryGetSyncable<PropSyncable>(data.syncId, out var syncable) && syncable.TryGetExtender<PuppetMasterExtender>(out var extender))
                {
                    // Save the most recent killed NPC
                    PuppetMasterExtender.LastKilled = syncable;

                    // Kill the puppet
                    PuppetMasterPatches.IgnorePatches = true;
                    extender.Component.Kill();
                    PuppetMasterPatches.IgnorePatches = false;
                }
            }
        }
    }
}
