using System.Numerics;
using Bonobo.Bepuphysics2;
using Bonobo.ECS.Core;
using DemoEngine.ECS;

namespace Game.BepuDemos.Demos;

/// <summary>
///     Fixed render-record registry shared by the ported demos: maps stable render ids to Bepu
///     bodies (mirrored into ECS entities, <c>ECS_PHYSICS_MAPPING</c>) or static poses, syncs the
///     authoritative Bepu poses into the ECS components after each null-dispatcher step and emits
///     the batched <see cref="Transform3DState"/> records in insertion order.
///
///     Compound children register the same parent <see cref="BodyHandle"/> with a child-local
///     <see cref="RigidPose"/>: the emitted world pose is parent ∘ local, so the client renders one
///     primitive per child without a new signal record type.
/// </summary>
internal sealed class DemoPoseSet
{
    private struct Record
    {
        public int RenderId;
        public Entity Entity;
        public BodyHandle Handle;
        public bool IsStatic;
        public Vector3 Position;
        public Quaternion Orientation;
        public Vector3 Scale;
        public RigidPose LocalPose;
        public bool HasLocalPose;
    }

    private readonly World _world;
    private readonly Simulation _simulation;
    private readonly List<Record> _records = new();
    private readonly Dictionary<int, int> _indexByRenderId = new();

    public DemoPoseSet(World world, Simulation simulation)
    {
        _world = world;
        _simulation = simulation;
    }

    public int Count => _records.Count;

    public void AddStatic(int renderId, Vector3 position, Quaternion orientation, Vector3 scale)
    {
        _indexByRenderId[renderId] = _records.Count;
        _records.Add(new Record
        {
            RenderId = renderId,
            IsStatic = true,
            Position = position,
            Orientation = orientation,
            Scale = scale,
        });
    }

    /// <summary>
    ///     Registers a dynamic record backed by <paramref name="handle"/>. When
    ///     <paramref name="localPose"/> is supplied the body is the compound parent and the record
    ///     emits the composed child pose.
    /// </summary>
    public void AddDynamic(int renderId, BodyHandle handle, Vector3 scale, RigidPose localPose = default, bool hasLocalPose = false)
    {
        var pose = _simulation.Bodies[handle].Pose;
        var entity = _world.Create(
            new Position3(pose.Position.X, pose.Position.Y, pose.Position.Z),
            new Rotation3(pose.Orientation.X, pose.Orientation.Y, pose.Orientation.Z, pose.Orientation.W),
            new Scale3(scale.X, scale.Y, scale.Z),
            new RenderId(renderId),
            new RenderLifecycle3 { State = EntityLifecycle3.Spawned },
            new PhysicsBody(handle));

        _indexByRenderId[renderId] = _records.Count;
        _records.Add(new Record
        {
            RenderId = renderId,
            Entity = entity,
            Handle = handle,
            Scale = scale,
            LocalPose = localPose,
            HasLocalPose = hasLocalPose,
        });
    }

    public bool Remove(int renderId, bool removeBody = false)
    {
        if (!_indexByRenderId.TryGetValue(renderId, out var index)) return false;

        var record = _records[index];
        if (!record.IsStatic)
        {
            if (removeBody) _simulation.Bodies.Remove(record.Handle);
            if (_world.IsAlive(record.Entity)) _world.Destroy(record.Entity);
        }

        _records.RemoveAt(index);
        _indexByRenderId.Remove(renderId);

        for (var i = index; i < _records.Count; i++) _indexByRenderId[_records[i].RenderId] = i;
        return true;
    }

    /// <summary>Removes every registered body (distinct handles only) and clears the registry.</summary>
    public void RemoveAllBodies()
    {
        var handles = new HashSet<BodyHandle>();
        foreach (var record in _records)
        {
            if (record.IsStatic) continue;
            if (handles.Add(record.Handle)) _simulation.Bodies.Remove(record.Handle);
        }

        Clear();
    }

    public void Clear()
    {
        foreach (var record in _records)
        {
            if (record.IsStatic) continue;
            if (_world.IsAlive(record.Entity)) _world.Destroy(record.Entity);
        }

        _records.Clear();
        _indexByRenderId.Clear();
    }

    public bool TryGetBody(int renderId, out BodyHandle handle)
    {
        handle = default;
        if (!_indexByRenderId.TryGetValue(renderId, out var index)) return false;
        if (_records[index].IsStatic) return false;
        handle = _records[index].Handle;
        return true;
    }

    /// <summary>Mirrors the authoritative Bepu poses into the ECS components (child poses composed).</summary>
    public void Sync()
    {
        for (var i = 0; i < _records.Count; i++)
        {
            var record = _records[i];
            if (record.IsStatic || !_world.IsAlive(record.Entity)) continue;

            var pose = _simulation.Bodies[record.Handle].Pose;
            if (record.HasLocalPose)
            {
                var local = record.LocalPose;
                var position = pose.Position + Vector3.Transform(local.Position, pose.Orientation);
                var orientation = Quaternion.Normalize(pose.Orientation * local.Orientation);
                pose = new RigidPose(position, orientation);
            }

            ref var pos = ref _world.Get<Position3>(record.Entity);
            pos.X = pose.Position.X;
            pos.Y = pose.Position.Y;
            pos.Z = pose.Position.Z;

            ref var rot = ref _world.Get<Rotation3>(record.Entity);
            rot.Qx = pose.Orientation.X;
            rot.Qy = pose.Orientation.Y;
            rot.Qz = pose.Orientation.Z;
            rot.Qw = pose.Orientation.W;
        }
    }

    /// <summary>Appends one <see cref="Transform3DState"/> per record, in insertion order.</summary>
    public void Emit(List<Transform3DState> states, double lifecycle = EntityLifecycle3.Active)
    {
        foreach (var record in _records)
        {
            Vector3 position;
            Quaternion orientation;
            if (record.IsStatic)
            {
                position = record.Position;
                orientation = record.Orientation;
            }
            else if (_world.IsAlive(record.Entity))
            {
                var p = _world.Get<Position3>(record.Entity);
                var r = _world.Get<Rotation3>(record.Entity);
                position = new Vector3(p.X, p.Y, p.Z);
                orientation = new Quaternion(r.Qx, r.Qy, r.Qz, r.Qw);
            }
            else
            {
                continue;
            }

            states.Add(new Transform3DState(
                record.RenderId,
                position.X, position.Y, position.Z,
                orientation.X, orientation.Y, orientation.Z, orientation.W,
                record.Scale.X, record.Scale.Y, record.Scale.Z,
                lifecycle));
        }
    }
}
