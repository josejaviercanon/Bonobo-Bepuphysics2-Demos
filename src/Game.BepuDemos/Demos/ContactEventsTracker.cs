using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Bonobo.Bepuphysics2;
using Bonobo.Bepuphysics2.Collidables;
using Bonobo.Bepuphysics2.CollisionDetection;
using Bonobo.Bepuphysics2.Constraints;
using Bonobo.BepuUtilities;
using Bonobo.BepuUtilities.Collections;
using Bonobo.BepuUtilities.Memory;

namespace Game.BepuDemos.Demos;

/// <summary>
///     Handlers for the contact events surfaced by <see cref="ContactEvents"/> (ported from the
///     upstream <c>ContactEventsDemo</c>, BepuPhysics2, Apache-2.0, Ross Nordby). BepuPhysics2 has
///     no built-in event concept — the narrow phase callbacks report manifold status and this
///     layer derives add/remove/touching/pair events from it.
/// </summary>
public interface IContactEventHandler
{
    void OnContactAdded<TManifold>(CollidableReference eventSource, CollidablePair pair, ref TManifold contactManifold,
        Vector3 contactOffset, Vector3 contactNormal, float depth, int featureId, int contactIndex, int workerIndex)
        where TManifold : unmanaged, IContactManifold<TManifold>
    {
    }

    void OnContactRemoved<TManifold>(CollidableReference eventSource, CollidablePair pair, ref TManifold contactManifold, int removedFeatureId, int workerIndex)
        where TManifold : unmanaged, IContactManifold<TManifold>
    {
    }

    void OnStartedTouching<TManifold>(CollidableReference eventSource, CollidablePair pair, ref TManifold contactManifold, int workerIndex)
        where TManifold : unmanaged, IContactManifold<TManifold>
    {
    }

    void OnTouching<TManifold>(CollidableReference eventSource, CollidablePair pair, ref TManifold contactManifold, int workerIndex)
        where TManifold : unmanaged, IContactManifold<TManifold>
    {
    }

    void OnStoppedTouching<TManifold>(CollidableReference eventSource, CollidablePair pair, ref TManifold contactManifold, int workerIndex)
        where TManifold : unmanaged, IContactManifold<TManifold>
    {
    }

    void OnPairCreated<TManifold>(CollidableReference eventSource, CollidablePair pair, ref TManifold contactManifold, int workerIndex)
        where TManifold : unmanaged, IContactManifold<TManifold>
    {
    }

    void OnPairUpdated<TManifold>(CollidableReference eventSource, CollidablePair pair, ref TManifold contactManifold, int workerIndex)
        where TManifold : unmanaged, IContactManifold<TManifold>
    {
    }

    void OnPairEnded(CollidableReference eventSource, CollidablePair pair)
    {
    }
}

/// <summary>
///     Watches a set of bodies and statics for contact changes and reports events (ported from
///     the upstream <c>ContactEventsDemo</c>). Single-threaded test bed: the worker-local pending
///     add caches collapse to one list flushed after the timestep.
/// </summary>
public class ContactEvents : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PreviousCollision
    {
        public CollidableReference Collidable;
        public bool Fresh;
        public bool WasTouching;
        public int ContactCount;
        public int FeatureId0;
        public int FeatureId1;
        public int FeatureId2;
        public int FeatureId3;
    }

    private Simulation _simulation = null!;
    private BufferPool _pool = null!;

    private CollidableProperty<int> _listenerIndices = null!;
    private IndexSet _staticListenerFlags;
    private IndexSet _bodyListenerFlags;
    private int _listenerCount;

    private struct Listener
    {
        public CollidableReference Source;
        public IContactEventHandler Handler;
        public QuickList<PreviousCollision> PreviousCollisions;
    }

    private Listener[] _listeners;

    private struct PendingWorkerAdd
    {
        public int ListenerIndex;
        public PreviousCollision Collision;
    }

    private QuickList<PendingWorkerAdd>[] _pendingWorkerAdds = null!;

    public ContactEvents(int initialListenerCapacity = 64)
    {
        _listeners = new Listener[initialListenerCapacity];
    }

    public void Initialize(Simulation simulation)
    {
        _simulation = simulation;
        _pool = simulation.BufferPool;
        simulation.Timestepper.BeforeCollisionDetection += SetFreshnessForCurrentActivityStatus;
        _listenerIndices = new CollidableProperty<int>(simulation, _pool);
        _pendingWorkerAdds = new QuickList<PendingWorkerAdd>[1];
    }

    public void Register(CollidableReference collidable, IContactEventHandler handler)
    {
        Debug.Assert(!IsListener(collidable), "Should only try to register listeners that weren't previously registered");
        if (collidable.Mobility == CollidableMobility.Static)
            _staticListenerFlags.Add(collidable.RawHandleValue, _pool);
        else
            _bodyListenerFlags.Add(collidable.RawHandleValue, _pool);

        if (_listenerCount >= _listeners.Length) Array.Resize(ref _listeners, _listeners.Length * 2);

        _listeners[_listenerCount] = new Listener { Handler = handler, Source = collidable };
        _listenerIndices[collidable] = _listenerCount;
        ++_listenerCount;
    }

    public void Register(BodyHandle body, IContactEventHandler handler) =>
        Register(_simulation.Bodies[body].CollidableReference, handler);

    public bool IsListener(CollidableReference collidable) =>
        collidable.Mobility == CollidableMobility.Static
            ? _staticListenerFlags.Contains(collidable.RawHandleValue)
            : _bodyListenerFlags.Contains(collidable.RawHandleValue);

    /// <summary>
    ///     Snapshots activity states just before collision detection so sleeping/static pairs are
    ///     not reported as stopped colliding by the flush.
    /// </summary>
    private void SetFreshnessForCurrentActivityStatus(float dt, IThreadDispatcher threadDispatcher)
    {
        var bodyHandleToLocation = _simulation.Bodies.HandleToLocation;
        for (var listenerIndex = 0; listenerIndex < _listenerCount; ++listenerIndex)
        {
            ref var listener = ref _listeners[listenerIndex];
            var source = listener.Source;
            var sourceExpectsUpdates = source.Mobility != CollidableMobility.Static
                                       && bodyHandleToLocation[source.BodyHandle.Value].SetIndex == 0;
            if (sourceExpectsUpdates)
            {
                var previousCollisions = _listeners[listenerIndex].PreviousCollisions;
                for (var j = 0; j < previousCollisions.Count; ++j) previousCollisions[j].Fresh = false;
            }
            else
            {
                var previousCollisions = _listeners[listenerIndex].PreviousCollisions;
                for (var j = 0; j < previousCollisions.Count; ++j)
                {
                    ref var previousCollision = ref previousCollisions[j];
                    previousCollision.Fresh = previousCollision.Collidable.Mobility == CollidableMobility.Static
                                              || bodyHandleToLocation[previousCollision.Collidable.BodyHandle.Value].SetIndex > 0;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UpdatePreviousCollision<TManifold>(ref PreviousCollision collision, ref TManifold manifold, bool isTouching)
        where TManifold : unmanaged, IContactManifold<TManifold>
    {
        Debug.Assert(manifold.Count <= 4, "Nonconvex manifolds are assumed to have a maximum of four contacts.");
        for (var j = 0; j < manifold.Count; ++j) Unsafe.Add(ref collision.FeatureId0, j) = manifold.GetFeatureId(j);
        collision.ContactCount = manifold.Count;
        collision.Fresh = true;
        collision.WasTouching = isTouching;
    }

    private void HandleManifoldForCollidable<TManifold>(int workerIndex, CollidableReference source, CollidableReference other, CollidablePair pair, ref TManifold manifold)
        where TManifold : unmanaged, IContactManifold<TManifold>
    {
        if (!IsListener(source)) return;

        var listenerIndex = _listenerIndices[source];
        ref var listener = ref _listeners[listenerIndex];

        var previousCollisionIndex = -1;
        var isTouching = false;
        for (var i = 0; i < listener.PreviousCollisions.Count; ++i)
        {
            ref var collision = ref listener.PreviousCollisions[i];
            if (collision.Collidable.Packed != other.Packed) continue;

            previousCollisionIndex = i;
            var previousContactsStillExist = 0;
            for (var contactIndex = 0; contactIndex < manifold.Count; ++contactIndex)
            {
                var featureId = manifold.GetFeatureId(contactIndex);
                var featureIdWasInPreviousCollision = false;
                for (var previousContactIndex = 0; previousContactIndex < collision.ContactCount; ++previousContactIndex)
                {
                    if (featureId != Unsafe.Add(ref collision.FeatureId0, previousContactIndex)) continue;
                    featureIdWasInPreviousCollision = true;
                    previousContactsStillExist |= 1 << previousContactIndex;
                    break;
                }

                if (!featureIdWasInPreviousCollision)
                {
                    manifold.GetContact(contactIndex, out var offset, out var normal, out var depth, out _);
                    listener.Handler.OnContactAdded(source, pair, ref manifold, offset, normal, depth, featureId, contactIndex, workerIndex);
                }

                if (manifold.GetDepth(contactIndex) >= 0) isTouching = true;
            }

            if (previousContactsStillExist != (1 << collision.ContactCount) - 1)
            {
                for (var previousContactIndex = 0; previousContactIndex < collision.ContactCount; ++previousContactIndex)
                {
                    if ((previousContactsStillExist & (1 << previousContactIndex)) == 0)
                        listener.Handler.OnContactRemoved(source, pair, ref manifold, Unsafe.Add(ref collision.FeatureId0, previousContactIndex), workerIndex);
                }
            }

            if (!collision.WasTouching && isTouching) listener.Handler.OnStartedTouching(source, pair, ref manifold, workerIndex);
            else if (collision.WasTouching && !isTouching) listener.Handler.OnStoppedTouching(source, pair, ref manifold, workerIndex);
            if (isTouching) listener.Handler.OnTouching(source, pair, ref manifold, workerIndex);

            UpdatePreviousCollision(ref collision, ref manifold, isTouching);
            break;
        }

        if (previousCollisionIndex < 0)
        {
            ref var addsForWorker = ref _pendingWorkerAdds[workerIndex];
            addsForWorker.EnsureCapacity(Math.Max(addsForWorker.Count + 1, 64), _pool);
            ref var pendingAdd = ref addsForWorker.AllocateUnsafely();
            pendingAdd.ListenerIndex = listenerIndex;
            pendingAdd.Collision.Collidable = other;
            listener.Handler.OnPairCreated(source, pair, ref manifold, workerIndex);
            for (var i = 0; i < manifold.Count; ++i)
            {
                manifold.GetContact(i, out var offset, out var normal, out var depth, out var featureId);
                listener.Handler.OnContactAdded(source, pair, ref manifold, offset, normal, depth, featureId, i, workerIndex);
                if (depth >= 0) isTouching = true;
            }

            if (isTouching)
            {
                listener.Handler.OnStartedTouching(source, pair, ref manifold, workerIndex);
                listener.Handler.OnTouching(source, pair, ref manifold, workerIndex);
            }

            UpdatePreviousCollision(ref pendingAdd.Collision, ref manifold, isTouching);
        }

        listener.Handler.OnPairUpdated(source, pair, ref manifold, workerIndex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void HandleManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold)
        where TManifold : unmanaged, IContactManifold<TManifold>
    {
        HandleManifoldForCollidable(workerIndex, pair.A, pair.B, pair, ref manifold);
        HandleManifoldForCollidable(workerIndex, pair.B, pair.A, pair, ref manifold);
    }

    private struct EmptyManifold : IContactManifold<EmptyManifold>
    {
        public int Count => 0;
        public bool Convex => true;
        public Contact this[int contactIndex] { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public static ref ConvexContact GetConvexContactReference(ref EmptyManifold manifold, int contactIndex) => throw new NotImplementedException();
        public static ref float GetDepthReference(ref EmptyManifold manifold, int contactIndex) => throw new NotImplementedException();
        public static ref int GetFeatureIdReference(ref EmptyManifold manifold, int contactIndex) => throw new NotImplementedException();
        public static ref Contact GetNonconvexContactReference(ref EmptyManifold manifold, int contactIndex) => throw new NotImplementedException();
        public static ref Vector3 GetNormalReference(ref EmptyManifold manifold, int contactIndex) => throw new NotImplementedException();
        public static ref Vector3 GetOffsetReference(ref EmptyManifold manifold, int contactIndex) => throw new NotImplementedException();
        public void GetContact(int contactIndex, out Vector3 offset, out Vector3 normal, out float depth, out int featureId) => throw new NotImplementedException();
        public void GetContact(int contactIndex, out Contact contactData) => throw new NotImplementedException();
        public float GetDepth(int contactIndex) => throw new NotImplementedException();
        public int GetFeatureId(int contactIndex) => throw new NotImplementedException();
        public Vector3 GetNormal(int contactIndex) => throw new NotImplementedException();
        public Vector3 GetOffset(int contactIndex) => throw new NotImplementedException();
    }

    public void Flush()
    {
        for (var i = 0; i < _listenerCount; ++i)
        {
            ref var listener = ref _listeners[i];
            for (var j = listener.PreviousCollisions.Count - 1; j >= 0; --j)
            {
                ref var collision = ref listener.PreviousCollisions[j];
                if (!collision.Fresh)
                {
                    CollidablePair pair;
                    NarrowPhase.SortCollidableReferencesForPair(listener.Source, collision.Collidable, out _, out _, out pair.A, out pair.B);
                    if (collision.ContactCount > 0)
                    {
                        var emptyManifold = new EmptyManifold();
                        for (var previousContactCount = 0; previousContactCount < collision.ContactCount; ++previousContactCount)
                            listener.Handler.OnContactRemoved(listener.Source, pair, ref emptyManifold, Unsafe.Add(ref collision.FeatureId0, previousContactCount), 0);
                        if (collision.WasTouching) listener.Handler.OnStoppedTouching(listener.Source, pair, ref emptyManifold, 0);
                    }

                    listener.Handler.OnPairEnded(collision.Collidable, pair);
                    listener.PreviousCollisions.FastRemoveAt(j);
                    if (listener.PreviousCollisions.Count == 0)
                    {
                        listener.PreviousCollisions.Dispose(_pool);
                        listener.PreviousCollisions = default;
                    }
                }
                else
                {
                    collision.Fresh = false;
                }
            }
        }

        for (var i = 0; i < _pendingWorkerAdds.Length; ++i)
        {
            ref var pendingAdds = ref _pendingWorkerAdds[i];
            for (var j = 0; j < pendingAdds.Count; ++j)
            {
                ref var add = ref pendingAdds[j];
                ref var collisions = ref _listeners[add.ListenerIndex].PreviousCollisions;
                collisions.EnsureCapacity(Math.Max(8, collisions.Count + 1), _pool);
                collisions.AllocateUnsafely() = pendingAdds[j].Collision;
            }

            if (pendingAdds.Span.Allocated) pendingAdds.Dispose(_pool);
            pendingAdds = default;
        }
    }

    public void Dispose()
    {
        if (_bodyListenerFlags.Flags.Allocated) _bodyListenerFlags.Dispose(_pool);
        if (_staticListenerFlags.Flags.Allocated) _staticListenerFlags.Dispose(_pool);
        _listenerIndices.Dispose();
        _simulation.Timestepper.BeforeCollisionDetection -= SetFreshnessForCurrentActivityStatus;
        for (var i = 0; i < _pendingWorkerAdds.Length; ++i)
            Debug.Assert(!_pendingWorkerAdds[i].Span.Allocated, "The pending worker adds should have been disposed by the previous flush.");
    }
}

/// <summary>Narrow phase callbacks that route manifold updates into a <see cref="ContactEvents"/>.</summary>
public struct ContactEventCallbacks : INarrowPhaseCallbacks
{
    private ContactEvents _events;

    public ContactEventCallbacks(ContactEvents events)
    {
        _events = events;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin) => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold, out PairMaterialProperties pairMaterial)
        where TManifold : unmanaged, IContactManifold<TManifold>
    {
        pairMaterial.FrictionCoefficient = 1f;
        pairMaterial.MaximumRecoveryVelocity = 2f;
        pairMaterial.SpringSettings = new SpringSettings(30f, 1f);
        _events.HandleManifold(workerIndex, pair, ref manifold);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB, ref ConvexContactManifold manifold) => true;

    public void Initialize(Simulation simulation) => _events.Initialize(simulation);

    public void Dispose()
    {
    }
}
