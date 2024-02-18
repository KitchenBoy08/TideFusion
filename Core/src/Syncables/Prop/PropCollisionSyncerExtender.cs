﻿using LabFusion.Extensions;
using LabFusion.MonoBehaviours;
using LabFusion.Utilities;
using SLZ.Interaction;
using UnityEngine;

namespace LabFusion.Syncables
{
    public class PropCollisionSyncerExtender : IPropExtender
    {
        public PropSyncable PropSyncable { get; set; }

        public CollisionSyncer Component;

        public bool ValidateExtender(PropSyncable syncable)
        {
            if (syncable.GameObjectCount > 0)
            {
                PropSyncable = syncable;
                Component = PropSyncable.TempRigidbodies.Items[0].GameObject.AddComponent<CollisionSyncer>();
                Component.enabled = false;
                return true;
            }

            return false;
        }

        public void OnCleanup()
        {
            if (!Component.IsNOC())
            {
                GameObject.Destroy(Component);
            }
        }

        public virtual void OnOwnedUpdate() { }

        public virtual void OnReceivedUpdate() { }

        public virtual void OnOwnershipTransfer() { }

        public virtual void OnUpdate() { }

        public virtual void OnAttach(Hand hand, Grip grip) 
        {
            if (hand.manager.IsSelf())
            {
                Component.enabled = true;
            }
        }

        public virtual void OnDetach(Hand hand, Grip grip) 
        { 
            if (!PropSyncable.IsGrabbedBy(hand.manager))
            {
                Component.enabled = false;
            }
        }

        public virtual void OnHeld() { }
    }
}
