﻿using System;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;

using LabFusion.Data;
using LabFusion.Debugging;
using LabFusion.Extensions;
using LabFusion.Grabbables;
using LabFusion.Network;
using LabFusion.Representation;
using LabFusion.Senders;
using LabFusion.Utilities;

using SLZ.Interaction;
using SLZ.Marrow.Pool;
using SLZ.Utilities;

using UnityEngine;

using SLZ.Rig;
using System.Security.Cryptography.Xml;

namespace LabFusion.Syncables
{
    public class PropSyncable : Syncable
    {
        private enum SendState
        {
            IDLE = 0,
            SENDING = 1,
        }

        public const float MinMoveMagnitude = 0.005f;
        public const float MinMoveSqrMagnitude = MinMoveMagnitude * MinMoveMagnitude;
        public const float MinMoveAngle = 0.15f;

        public static readonly FusionComponentCache<GameObject, PropSyncable> Cache = new FusionComponentCache<GameObject, PropSyncable>();
        public static readonly FusionComponentCache<GameObject, PropSyncable> HostCache = new FusionComponentCache<GameObject, PropSyncable>();

        public bool IsVehicle = false;

        public int GameObjectCount => TempRigidbodies != null ? TempRigidbodies.Length : 0;

        public Grip[] PropGrips;
        public FixedJoint[] LockJoints;
        public PDController[] PDControllers;

        public TempRigidbodyList TempRigidbodies;
        public TransformCache[] TransformCaches;
        public RigidbodyCache[] RigidbodyCaches;

        public AssetPoolee AssetPoolee;

        public readonly GameObject GameObject;

        public bool IsRootEnabled;

        public PropLifeCycleEvents LifeCycleEvents;

        public bool DisableSyncing = false;
        public bool HasIgnoreHierarchy;

        public float TimeOfMessage = 0f;

        public bool IsSleeping = false;

        public byte? Owner = null;

        // Target info
        public Vector3?[] InitialPositions;
        public Quaternion?[] InitialRotations;

        public Vector3?[] DesiredPositions;
        public Quaternion?[] DesiredRotations;
        public Vector3?[] DesiredVelocities;
        public Vector3?[] DesiredAngularVelocities;

        // Last sent info
        public Vector3[] LastSentPositions;
        public Quaternion[] LastSentRotations;

        private bool _verifyRigidbodies;

        private bool _isLockingDirty = false;
        private bool _lockedState = false;

        private bool _isIgnoringForces = false;

        private SendState _sendingState = SendState.IDLE;
        private float _timeOfLastSend = 0f;

        private IReadOnlyList<IPropExtender> _extenders;

        private List<IOwnerLocker> _ownerLockers;

        private GrabbedGripList _grabbedGrips;

        public bool IsHeld = false;

        private bool _initialized = false;

        public PropSyncable(InteractableHost host = null, GameObject root = null)
        {
            if (root != null)
                GameObject = root;
            else if (host != null)
                GameObject = host.GetSyncRoot();

            if (Cache.TryGet(GameObject, out var syncable))
                SyncManager.RemoveSyncable(syncable);

            Cache.Add(GameObject, this);

            Init();
        }

        private void OnInitGrips()
        {
            // Assign grip, rigidbody, etc. info
            if (GameObject)
                AssignInformation(GameObject);

            foreach (var grip in PropGrips)
            {
                grip.attachedHandDelegate += (Grip.HandDelegate)((h) => { OnAttach(h, grip); });
                grip.detachedHandDelegate += (Grip.HandDelegate)((h) => { OnDetach(h, grip); });
            }
        }

        private void OnInitRigidbodies()
        {
            // Setup target arrays
            InitialPositions = new Vector3?[GameObjectCount];
            InitialRotations = new Quaternion?[GameObjectCount];
            DesiredPositions = new Vector3?[GameObjectCount];
            DesiredRotations = new Quaternion?[GameObjectCount];
            DesiredVelocities = new Vector3?[GameObjectCount];
            DesiredAngularVelocities = new Vector3?[GameObjectCount];

            LastSentPositions = new Vector3[GameObjectCount];
            LastSentRotations = new Quaternion[GameObjectCount];

            // Setup gameobject arrays
            TransformCaches = new TransformCache[GameObjectCount];
            RigidbodyCaches = new RigidbodyCache[GameObjectCount];
            PDControllers = new PDController[GameObjectCount];

            DestroyLockJoints();
            LockJoints = new FixedJoint[GameObjectCount];

            for (var i = 0; i < GameObjectCount; i++)
            {
                // Clear out potential conflicting syncables
                var go = TempRigidbodies.Items[i].GameObject;
                if (HostCache.TryGet(go, out var conflict))
                {
                    if (conflict == this)
                        HostCache.Remove(go);
                    else
                        SyncManager.RemoveSyncable(conflict);
                }

                // Get the GameObject info
                HostCache.Add(go, this);

                PDControllers[i] = new PDController();

                // Initialize transform caches
                TransformCaches[i] = new TransformCache();
                RigidbodyCaches[i] = new RigidbodyCache();

                TransformCaches[i].FixedUpdate(TempRigidbodies.Items[i].Transform);
                RigidbodyCaches[i].FixedUpdate(TempRigidbodies.Items[i].Rigidbody);
            }
        }

        public void Init()
        {
            if (_initialized || IsDestroyed())
                return;

            AssetPoolee = AssetPoolee.Cache.Get(GameObject);

            LifeCycleEvents = GameObject.AddComponent<PropLifeCycleEvents>();
            LifeCycleEvents.enabled = false;
            LifeCycleEvents.Syncable = this;
            LifeCycleEvents.enabled = true;

            // Recreate all rigidbodies incase of them being gone (ascent Amber ball, looking at you)
            var tempHosts = GameObject.GetComponentsInChildren<InteractableHost>(true);
            foreach (var tempHost in tempHosts)
            {
                if (tempHost.IsStatic)
                    continue;

                // Remove from key lists
                if (KeyReciever.ClaimedHosts != null)
                {
                    bool removed = KeyReciever.ClaimedHosts.Remove(tempHost.TryCast<IGrippable>());

                    // If this was in a socket and we just removed it, recreate the rigidbody
                    if (removed)
                    {
                        tempHost.CreateRigidbody();
                        tempHost.EnableInteraction();
                    }
                }
            }

            OnInitGrips();
            OnInitRigidbodies();

            HasIgnoreHierarchy = GameObject.GetComponentInParent<IgnoreHierarchy>(true);

            _ownerLockers = new();
            _extenders = PropExtenderManager.GetPropExtenders(this);

            _initialized = true;
        }

        private void DestroyLockJoints()
        {
            if (LockJoints == null)
                return;

            for (var i = 0; i < LockJoints.Length; i++)
            {
                if (LockJoints[i] != null)
                    GameObject.Destroy(LockJoints[i]);
            }
        }

        public void AddOwnerLocker(IOwnerLocker locker)
        {
            if (_ownerLockers == null)
            {
#if DEBUG
                FusionLogger.Warn("Tried to add an owner locker but the list was null!");
#endif
                return;
            }

            if (!_ownerLockers.Contains(locker))
            {
                _ownerLockers.Add(locker);

#if DEBUG
                FusionLogger.Log("Added owner locker!");
#endif
            }
        }

        public bool TryGetExtender<T>(out T extender) where T : IPropExtender
        {
            foreach (var found in _extenders)
            {
                if (found.GetType() == typeof(T))
                {
                    extender = (T)found;
                    return true;
                }
            }

            extender = default;
            return false;
        }

        public bool HasExtender<T>() where T : IPropExtender
        {
            foreach (var found in _extenders)
            {
                if (found.GetType() == typeof(T))
                {
                    return true;
                }
            }

            return false;
        }

        private void AssignInformation(GameObject go)
        {
            PropGrips = go.GetComponentsInChildren<Grip>(true);

            TempRigidbodies = new TempRigidbodyList();
            TempRigidbodies.WriteComponents(go);

            _grabbedGrips = new GrabbedGripList(this, PropGrips.Length);
        }

        public void OnTransferOwner(Hand hand)
        {
            // Check if we're locked
            if (_ownerLockers.CheckLocks(out var owner) && owner != PlayerIdManager.LocalSmallId)
            {
                return;
            }

            // Determine the manager
            // Main player
            if (hand.manager.IsSelf())
            {
                PropSender.SendOwnershipTransfer(this);
            }

            _verifyRigidbodies = true;
        }

        public void OnAttach(Hand hand, Grip grip)
        {
            if (IsDestroyed())
                return;

            OnTransferOwner(hand);

            _grabbedGrips.OnGripAttach(hand, grip);

            foreach (var extender in _extenders)
                extender.OnAttach(hand, grip);
        }

        public void OnDetach(Hand hand, Grip grip)
        {
            if (IsDestroyed())
                return;

            OnTransferOwner(hand);

            _grabbedGrips.OnGripDetach(hand, grip);

            foreach (var extender in _extenders)
                extender.OnDetach(hand, grip);
        }

        public override void Cleanup()
        {
            if (IsDestroyed())
            {
#if DEBUG
                FusionLogger.Warn("Tried destroying a PropSyncable, but it was already destroyed!");
#endif
                return;
            }

            if (!GameObject.IsNOC())
            {
                Cache.Remove(GameObject);
            }

            if (!LifeCycleEvents.IsNOC())
            {
                GameObject.Destroy(LifeCycleEvents);
            }

            foreach (var item in TempRigidbodies.Items)
            {
                if (item.GameObject == null)
                    continue;

                HostCache.Remove(item.GameObject);
            }

            foreach (var extender in _extenders)
            {
                extender.OnCleanup();
            }

            DestroyLockJoints();

            base.Cleanup();
        }

        public override Grip GetGrip(ushort index)
        {
            if (PropGrips != null && PropGrips.Length > index)
                return PropGrips[index];
            return null;
        }

        public bool IsGrabbedBy(RigManager rigManager)
        {
            foreach (var grip in PropGrips)
            {
                foreach (var hand in grip.attachedHands)
                {
                    if (hand.HasAttachedObject() && hand.manager == rigManager)
                        return true;
                }
            }

            return false;
        }

        public override bool IsGrabbed()
        {
            foreach (var grip in PropGrips)
            {
                if (grip.attachedHands.Count > 0)
                    return true;
            }

            return false;
        }

        public override byte? GetOwner() => Owner;

        public override void SetOwner(byte owner)
        {
            // Make sure this has been initialized
            if (!_initialized)
            {
#if DEBUG
                FusionLogger.Warn("Tried setting the owner of a PropSyncable, but it wasn't initialized!");
#endif
                return;
            }

            // Make sure this isn't destroyed
            if (IsDestroyed())
            {
#if DEBUG
                FusionLogger.Warn("Tried setting the owner of a PropSyncable, but it was destroyed!");
#endif
                return;
            }

            // Reset position info
            if (Owner == null)
                FreezeValues();

            byte? prevOwner = Owner;

            Owner = owner;

            _isLockingDirty = true;
            _lockedState = false;

            RefreshMessageTime();

            // Notify extenders about ownership transfer
            if (prevOwner != Owner)
            {
                foreach (var extender in _extenders)
                    extender.OnOwnershipTransfer();
            }
        }

        public override void RemoveOwner()
        {
            Owner = null;
        }

        public void FreezeValues()
        {
            for (var i = 0; i < GameObjectCount; i++)
            {
                var transform = TransformCaches[i];

                DesiredPositions[i] = transform.Position;
                DesiredRotations[i] = transform.Rotation;
                DesiredVelocities[i] = Vector3Extensions.zero;
                DesiredAngularVelocities[i] = Vector3Extensions.zero;
                InitialPositions[i] = transform.Position;
                InitialRotations[i] = transform.Rotation;
            }
        }

        public void NullValues()
        {
            for (var i = 0; i < GameObjectCount; i++)
            {
                DesiredPositions[i] = null;
                DesiredRotations[i] = null;
                DesiredVelocities[i] = null;
                DesiredAngularVelocities[i] = null;
                InitialPositions[i] = null;
                InitialRotations[i] = null;

                PDControllers[i].Reset();
            }
        }

        public override bool IsOwner() => Owner.HasValue && Owner.Value == PlayerIdManager.LocalSmallId;

        public void SetRigidbodiesDirty()
        {
            _verifyRigidbodies = true;
        }

        public void VerifyRigidbodies()
        {
            if (_verifyRigidbodies)
            {
                // Check if any are missing
                bool needToUpdate = false;
                foreach (var item in TempRigidbodies.Items)
                {
                    if (item.Rigidbody.IsNOC())
                    {
                        needToUpdate = true;
                        break;
                    }
                }

                // Re-get all rigidbodies
                if (needToUpdate)
                {
                    for (var i = 0; i < GameObjectCount; i++)
                    {
                        var host = TempRigidbodies.Items[i].GameObject;

                        if (!host.IsNOC())
                            TempRigidbodies.Items[i].Rigidbody = host.GetComponent<Rigidbody>();

                        RigidbodyCaches[i].VerifyNull(TempRigidbodies.Items[i].Rigidbody);
                    }
                }

                _verifyRigidbodies = false;
            }
        }

        private void VerifyLocking()
        {
            if (_isLockingDirty)
            {
                for (var i = 0; i < GameObjectCount; i++)
                {
                    if (LockJoints[i] != null)
                        GameObject.Destroy(LockJoints[i]);

                    var rb = TempRigidbodies.Items[i].Rigidbody;
                    var gameObject = TempRigidbodies.Items[i].GameObject;
                    var transform = TempRigidbodies.Items[i].Transform;

                    var lockPos = InitialPositions[i];
                    var lockRot = InitialRotations[i];

                    if (rb && _lockedState && lockPos.HasValue && lockRot.HasValue)
                    {

                        transform.SetPositionAndRotation(lockPos.Value, lockRot.Value);

                        LockJoints[i] = gameObject.AddComponent<FixedJoint>();
                    }
                }

                _isLockingDirty = false;
            }
        }

        public override ushort? GetIndex(Grip grip)
        {
            for (ushort i = 0; i < PropGrips.Length; i++)
            {
                if (PropGrips[i] == grip)
                    return i;
            }
            return null;
        }

        public ushort? GetIndex(GameObject go)
        {
            for (ushort i = 0; i < GameObjectCount; i++)
            {
                if (TempRigidbodies.Items[i].GameObject == go)
                    return i;
            }
            return null;
        }

        public GameObject GetHost(ushort index)
        {
            if (GameObjectCount > index)
                return TempRigidbodies.Items[index].GameObject;
            return null;
        }

        private bool HasValidParameters() => !DisableSyncing && IsRegistered() && FusionSceneManager.IsLoadDone() && IsRootEnabled;

        public override void OnPreFixedUpdate()
        {
            if (!_initialized)
                return;

            if (!HasValidParameters())
                return;
            
            OnFixedUpdateCache();
        }

        public override void OnFixedUpdate()
        {
            if (!_initialized)
                return;

            try
            {
                if (!HasValidParameters())
                    return;

                if (!Owner.HasValue || Owner.Value == PlayerIdManager.LocalSmallId)
                    return;

                OnReceivedUpdate();
            }
            catch (Exception e)
            {
                if (e is NullReferenceException)
                    OnExceptionThrown();
#if !DEBUG
                else {
#endif
                    throw e;
#if !DEBUG
                }
#endif
            }
        }

        private void OnExceptionThrown()
        {
            for (var i = 0; i < RigidbodyCaches.Length; i++)
            {
                RigidbodyCaches[i].VerifyNull(TempRigidbodies.Items[i].Rigidbody);
            }
        }

        private void OnFixedUpdateCache()
        {
            for (var i = 0; i < TransformCaches.Length; i++)
            {
                TransformCaches[i].FixedUpdate(TempRigidbodies.Items[i].Transform);
                RigidbodyCaches[i].FixedUpdate(TempRigidbodies.Items[i].Rigidbody);
            }
        }

        private bool _readyToSend = false;

        public override void OnUpdate()
        {
            if (!_initialized)
                return;

            try
            {
                if (!HasValidParameters())
                    return;

                foreach (var extender in _extenders)
                    extender.OnUpdate();

                // Update grabbing for extenders
                if (IsHeld)
                {
                    foreach (var extender in _extenders)
                        extender.OnHeld();
                }

                VerifyRigidbodies();
                VerifyLocking();

                if (IsOwner())
                {
                    OnOwnedUpdate();
                }
            }
            catch (Exception e)
            {
                if (e is NullReferenceException)
                    OnExceptionThrown();
#if !DEBUG
                else {
#endif
                    throw e;
#if !DEBUG
                }
#endif
            }
        }

        public void PushUpdate()
        {
            _grabbedGrips.OnPushUpdate();
        }

        private bool HasMoved(int index)
        {
            var cache = TransformCaches[index];
            var lastPosition = LastSentPositions[index];
            var lastRotation = LastSentRotations[index];

            return (cache.Position - lastPosition).sqrMagnitude > MinMoveSqrMagnitude || Quaternion.Angle(cache.Rotation, lastRotation) > MinMoveAngle;
        }

        public bool IsMissingRigidbodies()
        {
            foreach (var cache in RigidbodyCaches)
            {
                if (cache.IsNull)
                    return true;
            }

            return false;
        }

        private void OnOwnedUpdate()
        {
            NullValues();

            bool hasMovingBody = false;

            for (var i = 0; i < GameObjectCount; i++)
            {
                var cache = RigidbodyCaches[i];

                if (cache.IsNull)
                {
                    continue;
                }

                var rb = TempRigidbodies.Items[i].Rigidbody;

                // Don't sync kinematic rigidbodies
                if (rb.isKinematic)
                    continue;

                if (!hasMovingBody && !cache.IsSleeping && HasMoved(i))
                {
                    hasMovingBody = true;
                    break;
                }
            }

            // Update send time
            if (hasMovingBody)
                _timeOfLastSend = TimeUtilities.TimeSinceStartup;

            // If a rigidbody has not moved within half a second, stop sending
            if (TimeUtilities.TimeSinceStartup - _timeOfLastSend >= 0.5f)
            {
                SetSendState(SendState.IDLE);
                _readyToSend = false;
                return;
            }
            else
            {
                SetSendState(SendState.SENDING);
                _readyToSend = true;
            }
        }

        public override void OnParallelUpdate()
        {
            if (!_readyToSend)
            {
                return;
            }

            for (var i = 0; i < TransformCaches.Length; i++)
            {
                var cache = TransformCaches[i];

                LastSentPositions[i] = cache.Position;
                LastSentRotations[i] = cache.Rotation;
            }

            using var writer = FusionWriter.Create(PropSyncableUpdateData.DefaultSize + (PropSyncableUpdateData.RigidbodySize * GameObjectCount));
            var data = PropSyncableUpdateData.Create(PlayerIdManager.LocalSmallId, this);
            writer.Write(data);

            using var message = FusionMessage.Create(NativeMessageTag.PropSyncableUpdate, writer);
            MessageSender.BroadcastMessageExceptSelf(NetworkChannel.Unreliable, message);

            _readyToSend = false;
        }

        private void SetSendState(SendState state)
        {
            if (_sendingState == state)
                return;

            switch (state)
            {
                default:
                case SendState.IDLE:
                    PropSender.SendSleep(this);
                    break;
                case SendState.SENDING:
                    break;
            }

            _sendingState = state;
        }

        public void RefreshMessageTime()
        {
            TimeOfMessage = TimeUtilities.TimeSinceStartup;
            IsSleeping = false;
        }

        private Vector3 _teleportOffset;

        private bool CheckForTeleport(Vector3 position, Vector3 velocity, Vector3 targetPosition)
        {
            // Teleport check
            var offset = targetPosition - position;

            float distSqr = offset.sqrMagnitude;
            if (distSqr > (2f * (velocity.sqrMagnitude + 1f)))
            {
                _teleportOffset = offset;

                return true;
            }

            return false;
        }

        private void Teleport(Vector3 offset)
        {
            var rootTransform = GameObject.transform;

            rootTransform.position += offset;
        }

        private bool _readyForForces = false;
        private bool _teleportThisFrame = false;

        public override void OnParallelFixedUpdate()
        {
            _readyForForces = false;
            _teleportThisFrame = false;

            if (!_initialized)
            {
                return;
            }

            if (!HasValidParameters())
            {
                return;
            }

            if (!Owner.HasValue || Owner.Value == PlayerIdManager.LocalSmallId)
            {
                return;
            }

            if (!SafetyUtilities.IsValidTime)
            {
                return;
            }

            float dt = TimeUtilities.FixedDeltaTime;
            float timeSinceMessage = TimeUtilities.TimeSinceStartup - TimeOfMessage;

            // Check if anything is being grabbed
            if (!IsHeld && (IsSleeping || timeSinceMessage >= 1f))
            {
                if (!_isIgnoringForces)
                {
                    // Set all desired values to nothing
                    for (var i = 0; i < GameObjectCount; i++)
                    {
                        DesiredPositions[i] = null;
                        DesiredRotations[i] = null;
                        DesiredVelocities[i] = null;
                        DesiredAngularVelocities[i] = null;
                    }

                    _isLockingDirty = true;
                    _lockedState = true;
                    _isIgnoringForces = true;
                }
                return;
            }

            if (_isIgnoringForces)
            {
                _isLockingDirty = true;
                _lockedState = false;
                _isIgnoringForces = false;
            }

            for (var i = 0; i < GameObjectCount; i++)
            {
                var rbCache = RigidbodyCaches[i];

                var pdController = PDControllers[i];

                if (rbCache.IsNull)
                {
                    pdController.Reset();
                    continue;
                }

                var cache = TransformCaches[i];

                if (!DesiredPositions[i].HasValue || !DesiredRotations[i].HasValue)
                {
                    pdController.Reset();
                    continue;
                }

                var pos = DesiredPositions[i].Value;
                var rot = DesiredRotations[i].Value;
                var vel = DesiredVelocities[i].Value;
                var angVel = DesiredAngularVelocities[i].Value;

                bool allowPosition = !HasIgnoreHierarchy;

                // Check if this is kinematic
                // If so, just ignore values
                if (rbCache.IsKinematic)
                {
                    pdController.Reset();
                    continue;
                }

                // Don't over predict
                if (timeSinceMessage <= 0.6f && !IsSleeping)
                {
                    // Move position with prediction
                    if (allowPosition)
                    {
                        pos += vel * dt;
                        DesiredPositions[i] = pos;
                    }
                }

                // Check for teleport
                if (i == 0 && allowPosition)
                {
                    _teleportThisFrame = CheckForTeleport(cache.Position, rbCache.Velocity, pos);
                }

                // Add proper forces
                if (allowPosition)
                {
                    pdController.SavedForce = pdController.GetForce(cache.Position, rbCache.Velocity, pos, vel);
                }
                else
                {
                    pdController.ResetPosition();
                }

                pdController.SavedTorque = pdController.GetTorque(cache.Rotation, rbCache.AngularVelocity, rot, angVel);

                _readyForForces = true;
            }
        }

        private void OnReceivedUpdate()
        {
            if (_teleportThisFrame)
            {
                Teleport(_teleportOffset);
            }

            if (_readyForForces)
            {
                for (var i = 0; i < GameObjectCount; i++)
                {
                    var rbCache = RigidbodyCaches[i];

                    if (rbCache.IsNull)
                    {
                        continue;
                    }

                    var rb = TempRigidbodies.Items[i].Rigidbody;

                    var pdController = PDControllers[i];

                    if (pdController.ValidPosition)
                    {
                        rb.AddForce(pdController.SavedForce, ForceMode.Acceleration);
                    }

                    if (pdController.ValidRotation)
                    {
                        rb.AddTorque(pdController.SavedTorque, ForceMode.Acceleration);
                    }
                }
            }
        }
    }
}
