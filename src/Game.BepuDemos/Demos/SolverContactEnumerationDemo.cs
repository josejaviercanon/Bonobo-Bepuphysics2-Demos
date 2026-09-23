using System.Diagnostics;
using System.Numerics;
using Bonobo.Bepuphysics2;
using Bonobo.Bepuphysics2.Collidables;
using Bonobo.Bepuphysics2.CollisionDetection;
using Bonobo.Bepuphysics2.Constraints;
using Bonobo.Bepuphysics2.Constraints.Contact;
using Bonobo.BepuUtilities;
using Bonobo.BepuUtilities.Collections;
using Bonobo.BepuUtilities.Memory;
using Bonobo.ECS.Core;
using DemoEngine.Config;
using DemoEngine.ECS;
using DemoEngine.Simulations;

namespace Game.BepuDemos.Demos;

/// <summary>
///     Port of the upstream <c>SolverContactEnumerationDemo</c> (BepuPhysics2, Apache-2.0,
///     Ross Nordby): a 20-row box pyramid drops onto a large sensor box; every step the
///     solver contacts connected to the sensor are extracted with an
///     <see cref="ISolverContactDataExtractor"/> and visualized as cylinders whose length
///     follows the penetration impulse and radius the friction impulse. Speculative contacts
///     (negative depth) route to the blue id range, touching contacts to the green range.
///
///     Test-bed deviation (documented in docs/compat-review.md): per-contact render color is
///     replaced by two id ranges (no color channel in `Transform3DState`).
///
///     Render-id ranges: 0 = deformed plane static, 1 = sensor box, 100+ = pyramid boxes,
///     1000+ = green contact visuals, 2000+ = blue speculative-contact visuals.
/// </summary>
public sealed class SolverContactEnumerationDemo : IDemoSimulation
{
    public const int PlaneRenderId = 0;
    public const int SensorRenderId = 1;
    public const int PyramidRenderIdBase = 100;
    public const int GreenContactRenderIdBase = 1000;
    public const int BlueContactRenderIdBase = 2000;

    public const int RowCount = 20;
    public const int PyramidCount = RowCount * (RowCount + 1) / 2;
    public const int MaxContactVisuals = 1024;

    public const int MaxTransformCount = 2 + PyramidCount + 2 * MaxContactVisuals;

    public const int BufferCapacity =
        SignalBuffer.HeaderLength + MaxTransformCount * SignalBufferLayout.Transform3DStride;

    public static DemoModule<Transform3DRenderSignal> CreateModule() => new(
        "solver-contact-enumeration",
        "solver-contact-enumeration",
        BufferCapacity,
        static (config, transport) => new SolverContactEnumerationDemo(config, transport),
        SignalBufferEncoders.ElementLength,
        SignalBufferEncoders.Encode);

    /// <summary>Readable form of a single extracted solver contact (upstream struct).</summary>
    private struct ExtractedContact
    {
        public Vector3 OffsetA;
        public float Depth;
        public Vector3 Normal;
        public float PenetrationImpulse;
        public float FrictionImpulseMagnitude;
    }

    /// <summary>Extracted contact manifold with its connected bodies (upstream struct).</summary>
    private struct ExtractedManifold
    {
        public QuickList<ExtractedContact> Contacts;
        public BodyHandle BodyA;
        public BodyHandle BodyB;

        public ExtractedManifold(BufferPool pool, BodyHandle a, BodyHandle b)
        {
            Contacts = new QuickList<ExtractedContact>(NonconvexContactManifold.MaximumContactCount, pool);
            BodyA = a;
            BodyB = b;
        }

        public ExtractedManifold(BufferPool pool, BodyHandle a) : this(pool, a, new BodyHandle(-1))
        {
        }

        public void Dispose(BufferPool pool)
        {
            Contacts.Dispose(pool);
        }
    }

    /// <summary>
    ///     Pulls contact data out of the solver into a simpler AOS representation (upstream
    ///     `SolverContactDataExtractor`, ported as-is).
    /// </summary>
    private struct SolverContactDataExtractor : ISolverContactDataExtractor
    {
        public QuickList<ExtractedManifold> Constraints;
        public BufferPool Pool;

        public SolverContactDataExtractor(BufferPool pool, int initialCapacity)
        {
            Pool = pool;
            Constraints = new QuickList<ExtractedManifold>(initialCapacity, pool);
        }

        private void ExtractConvexData<TPrestep, TAccumulatedImpulses>(
            ref ExtractedManifold constraintContacts, ref TPrestep prestep, ref TAccumulatedImpulses impulses)
            where TPrestep : struct, IConvexContactPrestep<TPrestep>
            where TAccumulatedImpulses : struct, IConvexContactAccumulatedImpulses<TAccumulatedImpulses>
        {
            Vector3Wide.ReadFirst(TPrestep.GetNormal(ref prestep), out var normal);

            float totalPenetrationImpulse = 0;
            for (var i = 0; i < TPrestep.ContactCount; ++i)
            {
                ref var sourceContact = ref TPrestep.GetContact(ref prestep, i);
                ref var targetContact = ref constraintContacts.Contacts.AllocateUnsafely();
                Vector3Wide.ReadFirst(sourceContact.OffsetA, out targetContact.OffsetA);
                targetContact.Depth = sourceContact.Depth[0];
                targetContact.Normal = normal;
                targetContact.PenetrationImpulse = TAccumulatedImpulses.GetPenetrationImpulseForContact(ref impulses, i)[0];
                totalPenetrationImpulse += targetContact.PenetrationImpulse;
            }

            Vector2Wide.ReadFirst(TAccumulatedImpulses.GetTangentFriction(ref impulses), out var tangentFriction);
            var twistFriction = TAccumulatedImpulses.GetTwistFriction(ref impulses)[0];
            var frictionMagnitudeApproximation = MathF.Sqrt(tangentFriction.LengthSquared() + twistFriction * twistFriction);
            var impulseScale = totalPenetrationImpulse > 0 ? frictionMagnitudeApproximation / totalPenetrationImpulse : 0;
            for (var i = 0; i < TPrestep.ContactCount; ++i)
            {
                ref var contact = ref constraintContacts.Contacts[i];
                contact.FrictionImpulseMagnitude = contact.PenetrationImpulse * impulseScale;
            }
        }

        public void ConvexOneBody<TPrestep, TAccumulatedImpulses>(BodyHandle bodyHandle, ref TPrestep prestep, ref TAccumulatedImpulses impulses)
            where TPrestep : struct, IConvexContactPrestep<TPrestep>
            where TAccumulatedImpulses : struct, IConvexContactAccumulatedImpulses<TAccumulatedImpulses>
        {
            ref var constraintContacts = ref Constraints.Allocate(Pool);
            constraintContacts = new ExtractedManifold(Pool, bodyHandle);
            ExtractConvexData(ref constraintContacts, ref prestep, ref impulses);
        }

        public void ConvexTwoBody<TPrestep, TAccumulatedImpulses>(BodyHandle bodyHandleA, BodyHandle bodyHandleB, ref TPrestep prestep, ref TAccumulatedImpulses impulses)
            where TPrestep : struct, ITwoBodyConvexContactPrestep<TPrestep>
            where TAccumulatedImpulses : struct, IConvexContactAccumulatedImpulses<TAccumulatedImpulses>
        {
            ref var constraintContacts = ref Constraints.Allocate(Pool);
            constraintContacts = new ExtractedManifold(Pool, bodyHandleA, bodyHandleB);
            ExtractConvexData(ref constraintContacts, ref prestep, ref impulses);
        }

        private void ExtractNonconvexData<TPrestep, TAccumulatedImpulses>(
            ref ExtractedManifold constraintContacts, ref TPrestep prestep, ref TAccumulatedImpulses impulses)
            where TPrestep : struct, INonconvexContactPrestep<TPrestep>
            where TAccumulatedImpulses : struct, INonconvexContactAccumulatedImpulses<TAccumulatedImpulses>
        {
            for (var i = 0; i < TPrestep.ContactCount; ++i)
            {
                ref var sourceContact = ref TPrestep.GetContact(ref prestep, i);
                ref var targetContact = ref constraintContacts.Contacts.AllocateUnsafely();
                Vector3Wide.ReadFirst(sourceContact.Offset, out targetContact.OffsetA);
                targetContact.Depth = sourceContact.Depth[0];
                Vector3Wide.ReadFirst(sourceContact.Normal, out targetContact.Normal);

                ref var contactImpulses = ref TAccumulatedImpulses.GetImpulsesForContact(ref impulses, i);
                targetContact.PenetrationImpulse = contactImpulses.Penetration[0];
                Vector2Wide.ReadFirst(contactImpulses.Tangent, out var tangentImpulses);
                targetContact.FrictionImpulseMagnitude = tangentImpulses.Length();
            }
        }

        public void NonconvexOneBody<TPrestep, TAccumulatedImpulses>(BodyHandle bodyHandle, ref TPrestep prestep, ref TAccumulatedImpulses impulses)
            where TPrestep : struct, INonconvexContactPrestep<TPrestep>
            where TAccumulatedImpulses : struct, INonconvexContactAccumulatedImpulses<TAccumulatedImpulses>
        {
            ref var constraintContacts = ref Constraints.Allocate(Pool);
            constraintContacts = new ExtractedManifold(Pool, bodyHandle);
            ExtractNonconvexData(ref constraintContacts, ref prestep, ref impulses);
        }

        public void NonconvexTwoBody<TPrestep, TAccumulatedImpulses>(BodyHandle bodyHandleA, BodyHandle bodyHandleB, ref TPrestep prestep, ref TAccumulatedImpulses impulses)
            where TPrestep : struct, ITwoBodyNonconvexContactPrestep<TPrestep>
            where TAccumulatedImpulses : struct, INonconvexContactAccumulatedImpulses<TAccumulatedImpulses>
        {
            ref var constraintContacts = ref Constraints.Allocate(Pool);
            constraintContacts = new ExtractedManifold(Pool, bodyHandleA, bodyHandleB);
            ExtractNonconvexData(ref constraintContacts, ref prestep, ref impulses);
        }

        public void Reset()
        {
            for (var i = 0; i < Constraints.Count; ++i)
            {
                Constraints[i].Dispose(Pool);
            }

            Constraints.Count = 0;
        }

        public void Dispose()
        {
            Reset();
            Constraints.Dispose(Pool);
        }
    }

    private readonly World _world;
    private readonly Simulation _simulation;
    private readonly BufferPool _bufferPool = new();
    private readonly IRenderTransport<Transform3DRenderSignal> _renderTransport;
    private readonly object _sync = new();

    private readonly DemoPoseSet _poses;
    private readonly BodyHandle _sensorHandle;
    private SolverContactDataExtractor _extractor;
    private long _seq;

    public SolverContactEnumerationDemo(
        DemoWorldConfig? config = null,
        IRenderTransport<Transform3DRenderSignal>? renderTransport = null)
    {
        _renderTransport = renderTransport ?? new CapturingRenderTransport<Transform3DRenderSignal>();

        _world = World.Create();
        _simulation = Simulation.Create(
            _bufferPool,
            new DemoNarrowPhaseCallbacks(new SpringSettings(30, 1)),
            new DemoPoseIntegratorCallbacks(new Vector3(0f, -10f, 0f)),
            new SolveDescription(8, 1));
        _poses = new DemoPoseSet(_world, _simulation);

        BuildSceneLocked();
        _sensorHandle = _simulation.Bodies.Add(
            BodyDescription.CreateConvexDynamic(new Vector3(0f, 0f, 1f), 10f, _simulation.Shapes, new Box(4f, 2f, 6f)));
        _poses.AddDynamic(SensorRenderId, _sensorHandle, new Vector3(4f, 2f, 6f));
        _extractor = new SolverContactDataExtractor(_bufferPool, 16);
    }

    public void Step(double deltaSeconds)
    {
        Transform3DRenderSignal signal;
        lock (_sync)
        {
            var stopwatch = Stopwatch.StartNew();
            _simulation.Timestep((float)deltaSeconds);
            stopwatch.Stop();

            _poses.Sync();
            signal = BuildSignalLocked(stopwatch.Elapsed.TotalMilliseconds);
        }

        _renderTransport.Push(signal);
    }

    private void BuildSceneLocked()
    {
        // Pyramid.
        var boxShape = new Box(1f, 1f, 1f);
        var boxInertia = boxShape.ComputeInertia(1f);
        var boxIndex = _simulation.Shapes.Add(boxShape);

        var pyramidIndex = 0;
        for (var rowIndex = 0; rowIndex < RowCount; ++rowIndex)
        {
            var columnCount = RowCount - rowIndex;
            for (var columnIndex = 0; columnIndex < columnCount; ++columnIndex)
            {
                var handle = _simulation.Bodies.Add(BodyDescription.CreateDynamic(
                    new Vector3(
                        (-columnCount * 0.5f + columnIndex) * boxShape.Width,
                        (rowIndex + 0.5f) * boxShape.Height + 10f,
                        0f),
                    boxInertia, boxIndex, 0.01f));
                _poses.AddDynamic(PyramidRenderIdBase + pyramidIndex++, handle, Vector3.One);
            }
        }

        // Deformed static plane (client rebuilds the same surface for presentation).
        const int planeWidth = 128;
        const int planeHeight = 128;
        var planeMesh = DemoMeshHelper.CreateDeformedPlane(planeWidth, planeHeight,
            static (x, y) => new Vector3(x - planeWidth / 2, MathF.Cos(x / 2f) * MathF.Sin(y / 2f), y - planeHeight / 2),
            new Vector3(2f, 1f, 2f), _bufferPool);
        var planePosition = new Vector3(0f, -2f, 0f);
        var planeOrientation = QuaternionEx.CreateFromAxisAngle(new Vector3(0f, 1f, 0f), MathF.PI / 2f);
        _simulation.Statics.Add(new StaticDescription(planePosition, planeOrientation, _simulation.Shapes.Add(planeMesh)));
        _poses.AddStatic(PlaneRenderId, planePosition, planeOrientation, new Vector3(planeWidth, 1f, planeHeight));
    }

    private Transform3DRenderSignal BuildSignalLocked(double tickMs)
    {
        ++_seq;

        _extractor.Reset();
        var sensorBody = _simulation.Bodies[_sensorHandle];
        for (var i = 0; i < sensorBody.Constraints.Count; ++i)
        {
            _simulation.NarrowPhase.TryExtractSolverContactData(sensorBody.Constraints[i].ConnectingConstraintHandle, ref _extractor);
        }

        var states = new List<Transform3DState>(MaxTransformCount);
        _poses.Emit(states);

        var greenCount = 0;
        var blueCount = 0;
        for (var manifoldIndex = 0; manifoldIndex < _extractor.Constraints.Count; ++manifoldIndex)
        {
            ref var constraintContacts = ref _extractor.Constraints[manifoldIndex];
            var bodyA = _simulation.Bodies[constraintContacts.BodyA];
            for (var contactIndex = 0; contactIndex < constraintContacts.Contacts.Count; ++contactIndex)
            {
                ref var contact = ref constraintContacts.Contacts[contactIndex];
                var contactPosition = contact.OffsetA + bodyA.Pose.Position;

                // Radius visualizes friction impulse, length the penetration impulse.
                const float baseLength = 0.2f;
                const float baseRadius = 0.1f;
                var speculative = contact.Depth < 0f;
                var radius = speculative ? baseRadius : baseRadius + MathF.Min(5f, contact.FrictionImpulseMagnitude * 0.1f);
                var length = speculative ? baseLength : baseLength + MathF.Min(5f, contact.PenetrationImpulse * 0.3f);
                var halfLength = length * 0.5f;

                Helpers.BuildOrthonormalBasis(contact.Normal, out var basisX, out var basisZ);
                var basis = new Matrix3x3 { X = basisX, Y = contact.Normal, Z = basisZ };
                QuaternionEx.CreateFromRotationMatrix(basis, out var orientation);

                int renderId;
                if (speculative)
                {
                    if (blueCount >= MaxContactVisuals) continue;
                    renderId = BlueContactRenderIdBase + blueCount++;
                }
                else
                {
                    if (greenCount >= MaxContactVisuals) continue;
                    renderId = GreenContactRenderIdBase + greenCount++;
                }

                var position = contactPosition + contact.Normal * halfLength;
                states.Add(new Transform3DState(
                    renderId,
                    position.X, position.Y, position.Z,
                    orientation.X, orientation.Y, orientation.Z, orientation.W,
                    radius * 2f, length, radius * 2f,
                    EntityLifecycle3.Active));
            }
        }

        return new Transform3DRenderSignal(_seq, states.Count, tickMs, states);
    }

    public void Dispose()
    {
        _extractor.Dispose();
        _simulation.Dispose();
        _bufferPool.Clear();
        World.Destroy(_world);
    }
}
