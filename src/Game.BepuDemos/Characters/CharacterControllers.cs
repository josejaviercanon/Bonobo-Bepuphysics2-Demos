using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Bonobo.Bepuphysics2;
using Bonobo.Bepuphysics2.Collidables;
using Bonobo.Bepuphysics2.CollisionDetection;
using Bonobo.BepuUtilities;

namespace Game.BepuDemos.Demos.Characters;

/// <summary>Raw data for a dynamic character controller instance (upstream <c>CharacterController</c>).</summary>
public struct CharacterController
{
    /// <summary>Direction the character is looking in world space. Defines the forward direction for movement.</summary>
    public Vector3 ViewDirection;

    /// <summary>
    ///     Target horizontal velocity. X is the desired velocity along the strafing direction,
    ///     Y is the desired velocity along the forward direction (both projected on the surface).
    /// </summary>
    public Vector2 TargetVelocity;

    /// <summary>If true, the character tries to jump on the next step. Reset to false after processing.</summary>
    public bool TryJump;

    public BodyHandle BodyHandle;
    public Vector3 LocalUp;
    public float JumpVelocity;
    public float MaximumHorizontalForce;
    public float MaximumVerticalForce;
    public float CosMaximumSlope;
    public float MinimumSupportDepth;
    public float MinimumSupportContinuationDepth;

    /// <summary>Whether the character is currently supported.</summary>
    public bool Supported;

    /// <summary>Collidable supporting the character, if any. Only valid if Supported is true.</summary>
    public CollidableReference Support;

    /// <summary>Handle of the character's motion constraint, if any. Only valid if Supported is true.</summary>
    public ConstraintHandle MotionConstraintHandle;
}

/// <summary>
///     System that manages all the characters in a simulation (upstream <c>CharacterControllers</c>,
///     BepuPhysics2, Apache-2.0, Ross Nordby): it updates movement constraints based on character
///     goals and contact states through the timestepper's BeforeCollisionDetection/CollisionsDetected
///     hooks and two custom solver constraints registered at initialization.
///
///     Test-bed deviations (documented in docs/compat-review.md): every solve is a
///     null-dispatcher single-threaded step, so the worker-cache machinery collapses to a single
///     sequential cache; pooled <c>Buffer</c>/<c>QuickList</c> storage is replaced by managed
///     arrays/lists preallocated in <see cref="PrepareForContacts"/> (no step-time allocations).
/// </summary>
public class CharacterControllers : IDisposable
{
    /// <summary>Gets the simulation to which this set of characters belongs.</summary>
    public Simulation Simulation { get; private set; } = null!;

    private int[] _bodyHandleToCharacterIndex = Array.Empty<int>();
    private CharacterController[] _characters = new CharacterController[16];
    private int _characterCount;

    /// <summary>Gets the number of characters being controlled.</summary>
    public int CharacterCount => _characterCount;

    /// <summary>Creates a character controller system.</summary>
    public CharacterControllers(int initialCharacterCapacity = 4096, int initialBodyHandleCapacity = 4096)
    {
        _characters = new CharacterController[Math.Max(1, initialCharacterCapacity)];
        _supportCandidates = new SupportCandidate[Math.Max(1, initialCharacterCapacity)];
        ResizeBodyHandleCapacity(initialBodyHandleCapacity);
    }

    /// <summary>Caches the simulation associated with the characters and registers the constraint types.</summary>
    public void Initialize(Simulation simulation)
    {
        Simulation = simulation;
        simulation.Solver.Register<DynamicCharacterMotionConstraint>();
        simulation.Solver.Register<StaticCharacterMotionConstraint>();
        simulation.Timestepper.BeforeCollisionDetection += PrepareForContacts;
        simulation.Timestepper.CollisionsDetected += AnalyzeContacts;
    }

    private void ResizeBodyHandleCapacity(int bodyHandleCapacity)
    {
        var oldCapacity = _bodyHandleToCharacterIndex.Length;
        if (bodyHandleCapacity <= oldCapacity) return;
        Array.Resize(ref _bodyHandleToCharacterIndex, bodyHandleCapacity);
        for (var i = oldCapacity; i < _bodyHandleToCharacterIndex.Length; ++i)
        {
            _bodyHandleToCharacterIndex[i] = -1;
        }
    }

    /// <summary>Ensures the body-handle mapping and character buffers can hold the requested sizes.</summary>
    public void EnsureCapacity(int characterCapacity, int bodyHandleCapacity)
    {
        if (_characters.Length < characterCapacity) Array.Resize(ref _characters, characterCapacity);
        if (_supportCandidates.Length < characterCapacity) Array.Resize(ref _supportCandidates, characterCapacity);
        ResizeBodyHandleCapacity(bodyHandleCapacity);
    }

    /// <summary>Gets the current memory slot index of a character using its associated body handle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetCharacterIndexForBodyHandle(int bodyHandle)
    {
        Debug.Assert(bodyHandle >= 0 && bodyHandle < _bodyHandleToCharacterIndex.Length && _bodyHandleToCharacterIndex[bodyHandle] >= 0,
            "Can only look up indices for body handles associated with characters in this CharacterControllers instance.");
        return _bodyHandleToCharacterIndex[bodyHandle];
    }

    /// <summary>Gets a reference to the character at the given memory slot index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref CharacterController GetCharacterByIndex(int index) => ref _characters[index];

    /// <summary>Gets a reference to the character using the handle of the character's body.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref CharacterController GetCharacterByBodyHandle(BodyHandle bodyHandle)
    {
        Debug.Assert(bodyHandle.Value >= 0 && bodyHandle.Value < _bodyHandleToCharacterIndex.Length && _bodyHandleToCharacterIndex[bodyHandle.Value] >= 0,
            "Can only look up indices for body handles associated with characters in this CharacterControllers instance.");
        return ref _characters[_bodyHandleToCharacterIndex[bodyHandle.Value]];
    }

    /// <summary>Allocates a character and returns a reference to it.</summary>
    public ref CharacterController AllocateCharacter(BodyHandle bodyHandle)
    {
        Debug.Assert(bodyHandle.Value >= 0 && (bodyHandle.Value >= _bodyHandleToCharacterIndex.Length || _bodyHandleToCharacterIndex[bodyHandle.Value] == -1),
            "Cannot allocate more than one character for the same body handle.");
        if (bodyHandle.Value >= _bodyHandleToCharacterIndex.Length)
            ResizeBodyHandleCapacity(Math.Max(bodyHandle.Value + 1, _bodyHandleToCharacterIndex.Length * 2));
        if (_characterCount >= _characters.Length)
        {
            var newCapacity = _characters.Length * 2;
            Array.Resize(ref _characters, newCapacity);
            Array.Resize(ref _supportCandidates, newCapacity);
        }

        var characterIndex = _characterCount++;
        ref var character = ref _characters[characterIndex];
        character = default;
        character.BodyHandle = bodyHandle;
        _bodyHandleToCharacterIndex[bodyHandle.Value] = characterIndex;
        return ref character;
    }

    /// <summary>Removes a character from the character controllers set by the character's index.</summary>
    public void RemoveCharacterByIndex(int characterIndex)
    {
        Debug.Assert(characterIndex >= 0 && characterIndex < _characterCount, "Character index must exist in the set of characters.");
        var bodyHandle = _characters[characterIndex].BodyHandle;
        _bodyHandleToCharacterIndex[bodyHandle.Value] = -1;
        var lastIndex = --_characterCount;
        if (characterIndex < lastIndex)
        {
            // Swap-remove: move the last character into the vacated slot and update its mapping.
            _characters[characterIndex] = _characters[lastIndex];
            _bodyHandleToCharacterIndex[_characters[characterIndex].BodyHandle.Value] = characterIndex;
        }
    }

    /// <summary>Removes a character from the character controllers set by the body handle associated with the character.</summary>
    public void RemoveCharacterByBodyHandle(BodyHandle bodyHandle)
    {
        Debug.Assert(bodyHandle.Value >= 0 && bodyHandle.Value < _bodyHandleToCharacterIndex.Length && _bodyHandleToCharacterIndex[bodyHandle.Value] >= 0,
            "Removing a character by body handle requires that a character associated with the given body handle actually exists.");
        RemoveCharacterByIndex(_bodyHandleToCharacterIndex[bodyHandle.Value]);
    }

    private struct SupportCandidate
    {
        public Vector3 OffsetFromCharacter;
        public float Depth;
        public Vector3 OffsetFromSupport;
        public Vector3 Normal;
        public CollidableReference Support;
    }

    private SupportCandidate[] _supportCandidates;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReportContacts<TManifold>(CollidableReference characterCollidable, CollidableReference supportCollidable, CollidablePair pair, ref TManifold manifold, int workerIndex)
        where TManifold : struct, IContactManifold<TManifold>
    {
        if (characterCollidable.Mobility == CollidableMobility.Dynamic && characterCollidable.BodyHandle.Value < _bodyHandleToCharacterIndex.Length)
        {
            var characterBodyHandle = characterCollidable.BodyHandle;
            var characterIndex = _bodyHandleToCharacterIndex[characterBodyHandle.Value];
            if (characterIndex >= 0)
            {
                // This is actually a character; process the manifold into a support representation.
                ref var character = ref _characters[characterIndex];

                // The body may be inactive during this callback; account for the current location.
                ref var bodyLocation = ref Simulation.Bodies.HandleToLocation[character.BodyHandle.Value];
                ref var set = ref Simulation.Bodies.Sets[bodyLocation.SetIndex];
                ref var pose = ref set.DynamicsState[bodyLocation.Index].Motion.Pose;
                QuaternionEx.Transform(character.LocalUp, pose.Orientation, out var up);
                if (manifold.Convex)
                {
                    ref var convexManifold = ref Unsafe.As<TManifold, ConvexContactManifold>(ref manifold);
                    var normalUpDot = Vector3.Dot(convexManifold.Normal, up);
                    // The narrow phase generates contacts with normals pointing from B to A; negate when the character is B.
                    if ((pair.B.Packed == characterCollidable.Packed ? -normalUpDot : normalUpDot) > character.CosMaximumSlope)
                    {
                        // Can the maximum depth contact be used as a support?
                        var maximumDepth = convexManifold.Contact0.Depth;
                        var maximumDepthIndex = 0;
                        for (var i = 1; i < convexManifold.Count; ++i)
                        {
                            ref var candidateDepth = ref Unsafe.Add(ref convexManifold.Contact0, i).Depth;
                            if (candidateDepth > maximumDepth)
                            {
                                maximumDepth = candidateDepth;
                                maximumDepthIndex = i;
                            }
                        }

                        if (maximumDepth >= character.MinimumSupportDepth || (character.Supported && maximumDepth > character.MinimumSupportContinuationDepth))
                        {
                            ref var supportCandidate = ref _supportCandidates[characterIndex];
                            if (supportCandidate.Depth < maximumDepth)
                            {
                                supportCandidate.Depth = maximumDepth;
                                ref var deepestContact = ref Unsafe.Add(ref convexManifold.Contact0, maximumDepthIndex);
                                var offsetFromB = deepestContact.Offset - convexManifold.OffsetB;
                                if (pair.B.Packed == characterCollidable.Packed)
                                {
                                    supportCandidate.Normal = -convexManifold.Normal;
                                    supportCandidate.OffsetFromCharacter = offsetFromB;
                                    supportCandidate.OffsetFromSupport = deepestContact.Offset;
                                }
                                else
                                {
                                    supportCandidate.Normal = convexManifold.Normal;
                                    supportCandidate.OffsetFromCharacter = deepestContact.Offset;
                                    supportCandidate.OffsetFromSupport = offsetFromB;
                                }

                                supportCandidate.Support = supportCollidable;
                            }
                        }
                    }
                }
                else
                {
                    ref var nonconvexManifold = ref Unsafe.As<TManifold, NonconvexContactManifold>(ref manifold);
                    // Nonconvex candidates can have different normals, so test every one.
                    var maximumDepth = float.MinValue;
                    var maximumDepthIndex = -1;
                    for (var i = 0; i < nonconvexManifold.Count; ++i)
                    {
                        ref var candidate = ref Unsafe.Add(ref nonconvexManifold.Contact0, i);
                        if (candidate.Depth > maximumDepth)
                        {
                            var upDot = Vector3.Dot(candidate.Normal, up);
                            if ((pair.B.Packed == characterCollidable.Packed ? -upDot : upDot) > character.CosMaximumSlope)
                            {
                                maximumDepth = candidate.Depth;
                                maximumDepthIndex = i;
                            }
                        }
                    }

                    if (maximumDepth >= character.MinimumSupportDepth || (character.Supported && maximumDepth > character.MinimumSupportContinuationDepth))
                    {
                        ref var supportCandidate = ref _supportCandidates[characterIndex];
                        if (supportCandidate.Depth < maximumDepth)
                        {
                            ref var deepestContact = ref Unsafe.Add(ref nonconvexManifold.Contact0, maximumDepthIndex);
                            supportCandidate.Depth = maximumDepth;
                            var offsetFromB = deepestContact.Offset - nonconvexManifold.OffsetB;
                            if (pair.B.Packed == characterCollidable.Packed)
                            {
                                supportCandidate.Normal = -deepestContact.Normal;
                                supportCandidate.OffsetFromCharacter = offsetFromB;
                                supportCandidate.OffsetFromSupport = deepestContact.Offset;
                            }
                            else
                            {
                                supportCandidate.Normal = deepestContact.Normal;
                                supportCandidate.OffsetFromCharacter = deepestContact.Offset;
                                supportCandidate.OffsetFromSupport = offsetFromB;
                            }

                            supportCandidate.Support = supportCollidable;
                        }
                    }
                }

                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Reports contacts about a collision to the character system. If the pair does not involve
    ///     a character or there are no contacts, does nothing and returns false.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryReportContacts<TManifold>(in CollidablePair pair, ref TManifold manifold, int workerIndex, ref PairMaterialProperties materialProperties)
        where TManifold : struct, IContactManifold<TManifold>
    {
        if (manifold.Count == 0)
            return false;
        // It's possible for neither, one, or both collidables to be a character.
        var aIsCharacter = TryReportContacts(pair.A, pair.B, pair, ref manifold, workerIndex);
        var bIsCharacter = TryReportContacts(pair.B, pair.A, pair, ref manifold, workerIndex);
        if (aIsCharacter || bIsCharacter)
        {
            // The character's motion over the surface is controlled entirely by the motion constraint.
            materialProperties.FrictionCoefficient = 0;
            return true;
        }

        return false;
    }

    private unsafe void ExpandBoundingBoxes(int start, int count)
    {
        var end = start + count;
        for (var i = start; i < end; ++i)
        {
            ref var character = ref _characters[i];
            var characterBody = Simulation.Bodies[character.BodyHandle];
            if (characterBody.Awake)
            {
                Simulation.BroadPhase.GetActiveBoundsPointers(characterBody.Collidable.BroadPhaseIndex, out var min, out var max);
                QuaternionEx.Transform(character.LocalUp, characterBody.Pose.Orientation, out var characterUp);
                var supportExpansion = character.MinimumSupportContinuationDepth * characterUp;
                *min += Vector3.Min(Vector3.Zero, supportExpansion);
                *max += Vector3.Max(Vector3.Zero, supportExpansion);
            }
        }
    }

    /// <summary>Preallocates space for support data collected during the narrow phase.</summary>
    private void PrepareForContacts(float dt, IThreadDispatcher? threadDispatcher)
    {
        if (_supportCandidates.Length < _characterCount) Array.Resize(ref _supportCandidates, _characterCount);
        for (var i = 0; i < _characterCount; ++i)
        {
            // Initialize the depths to a value that guarantees replacement.
            _supportCandidates[i].Depth = float.MinValue;
        }

        EnsureAnalyzeCacheCapacity();
        // While the character retains support with contacts above MinimumSupportContinuationDepth, the
        // broad phase needs expanded bounding boxes to still see that support collidable.
        ExpandBoundingBoxes(0, _characterCount);
    }

    private void EnsureAnalyzeCacheCapacity()
    {
        _constraintHandlesToRemove.EnsureCapacity(_characterCount);
        _dynamicConstraintsToAdd.EnsureCapacity(_characterCount);
        _staticConstraintsToAdd.EnsureCapacity(_characterCount);
        _jumps.EnsureCapacity(_characterCount);
    }

    private struct PendingDynamicConstraint
    {
        public int CharacterIndex;
        public DynamicCharacterMotionConstraint Description;
    }

    private struct PendingStaticConstraint
    {
        public int CharacterIndex;
        public StaticCharacterMotionConstraint Description;
    }

    private struct Jump
    {
        // Not every jump contains a support body; jumps are rare, so only capacity is wasted.
        public int CharacterBodyIndex;
        public Vector3 CharacterVelocityChange;
        public int SupportBodyIndex;
        public Vector3 SupportImpulseOffset;
    }

    private readonly List<ConstraintHandle> _constraintHandlesToRemove = new();
    private readonly List<PendingDynamicConstraint> _dynamicConstraintsToAdd = new();
    private readonly List<PendingStaticConstraint> _staticConstraintsToAdd = new();
    private readonly List<Jump> _jumps = new();

    private void AnalyzeContactsForCharacterRegion(int start, int exclusiveEnd, int workerIndex)
    {
        for (var characterIndex = start; characterIndex < exclusiveEnd; ++characterIndex)
        {
            // This iterates over both active and inactive characters rather than segmenting them.
            ref var character = ref _characters[characterIndex];
            ref var bodyLocation = ref Simulation.Bodies.HandleToLocation[character.BodyHandle.Value];
            if (bodyLocation.SetIndex == 0)
            {
                var supportCandidate = _supportCandidates[characterIndex];

                // Protect against the support body being removed (which removes the motion constraint too).
                if (character.Supported)
                {
                    if (!Simulation.Solver.ConstraintExists(character.MotionConstraintHandle) ||
                        (Simulation.Solver.HandleToConstraint[character.MotionConstraintHandle.Value].TypeId != DynamicCharacterMotionTypeProcessor.BatchTypeId &&
                         Simulation.Solver.HandleToConstraint[character.MotionConstraintHandle.Value].TypeId != StaticCharacterMotionTypeProcessor.BatchTypeId))
                    {
                        character.Supported = false;
                    }
                }

                // Remove the old constraint if any of the following hold:
                // 1) previously supported but no longer, 2) supported by a different body,
                // 3) static -> body support change, 4) body -> static support change.
                var shouldRemove = character.Supported && (character.TryJump || supportCandidate.Depth == float.MinValue || character.Support.Packed != supportCandidate.Support.Packed);
                if (shouldRemove)
                {
                    _constraintHandlesToRemove.Add(character.MotionConstraintHandle);
                }

                if (supportCandidate.Depth > float.MinValue && character.TryJump)
                {
                    QuaternionEx.Transform(character.LocalUp, Simulation.Bodies.ActiveSet.DynamicsState[bodyLocation.Index].Motion.Pose.Orientation, out var characterUp);
                    // We assume character orientations are constant for this approximation.
                    var characterUpVelocity = Vector3.Dot(Simulation.Bodies.ActiveSet.DynamicsState[bodyLocation.Index].Motion.Velocity.Linear, characterUp);
                    // Jumping targets the velocity change necessary to reach character.JumpVelocity along up.
                    if (character.Support.Mobility != CollidableMobility.Static)
                    {
                        ref var supportingBodyLocation = ref Simulation.Bodies.HandleToLocation[character.Support.BodyHandle.Value];
                        Debug.Assert(supportingBodyLocation.SetIndex == 0, "If the character is active, any support should be too.");
                        ref var supportVelocity = ref Simulation.Bodies.ActiveSet.DynamicsState[supportingBodyLocation.Index].Motion.Velocity;
                        var wxr = Vector3.Cross(supportVelocity.Angular, supportCandidate.OffsetFromSupport);
                        var supportContactVelocity = supportVelocity.Linear + wxr;
                        var supportUpVelocity = Vector3.Dot(supportContactVelocity, characterUp);

                        ref var jump = ref AddAndGetReference(_jumps);
                        jump.CharacterBodyIndex = bodyLocation.Index;
                        jump.CharacterVelocityChange = characterUp * MathF.Max(0, character.JumpVelocity - (characterUpVelocity - supportUpVelocity));
                        if (character.Support.Mobility == CollidableMobility.Dynamic)
                        {
                            jump.SupportBodyIndex = supportingBodyLocation.Index;
                            jump.SupportImpulseOffset = supportCandidate.OffsetFromSupport;
                        }
                        else
                        {
                            // No point in applying impulses to kinematics.
                            jump.SupportBodyIndex = -1;
                        }
                    }
                    else
                    {
                        // Static bodies have no velocity, so we don't have to consider the support.
                        ref var jump = ref AddAndGetReference(_jumps);
                        jump.CharacterBodyIndex = bodyLocation.Index;
                        jump.CharacterVelocityChange = characterUp * MathF.Max(0, character.JumpVelocity - characterUpVelocity);
                        jump.SupportBodyIndex = -1;
                    }

                    character.Supported = false;
                }
                else if (supportCandidate.Depth > float.MinValue)
                {
                    // A support exists: update the old constraint or add a new one.

                    // Project the view direction down onto the surface represented by the contact normal.
                    Matrix3x3 surfaceBasis;
                    surfaceBasis.Y = supportCandidate.Normal;
                    // Note negation: right handed basis where -Z is forward, +Z is backward.
                    QuaternionEx.Transform(character.LocalUp, Simulation.Bodies.ActiveSet.DynamicsState[bodyLocation.Index].Motion.Pose.Orientation, out var up);
                    var rayDistance = Vector3.Dot(character.ViewDirection, surfaceBasis.Y);
                    var rayVelocity = Vector3.Dot(up, surfaceBasis.Y);
                    Debug.Assert(rayVelocity > 0,
                        "The calibrated support normal and the character's up direction should have a positive dot product if the maximum slope is working properly. Is the maximum slope >= pi/2?");
                    surfaceBasis.Z = up * (rayDistance / rayVelocity) - character.ViewDirection;
                    var zLengthSquared = surfaceBasis.Z.LengthSquared();
                    if (zLengthSquared > 1e-12f)
                    {
                        surfaceBasis.Z /= MathF.Sqrt(zLengthSquared);
                    }
                    else
                    {
                        QuaternionEx.GetQuaternionBetweenNormalizedVectors(Vector3.UnitY, surfaceBasis.Y, out var rotation);
                        QuaternionEx.TransformUnitZ(rotation, out surfaceBasis.Z);
                    }

                    surfaceBasis.X = Vector3.Cross(surfaceBasis.Y, surfaceBasis.Z);
                    QuaternionEx.CreateFromRotationMatrix(surfaceBasis, out var surfaceBasisQuaternion);
                    if (supportCandidate.Support.Mobility != CollidableMobility.Static)
                    {
                        // The character is supported by a body.
                        var motionConstraint = new DynamicCharacterMotionConstraint
                        {
                            MaximumHorizontalForce = character.MaximumHorizontalForce,
                            MaximumVerticalForce = character.MaximumVerticalForce,
                            OffsetFromCharacterToSupportPoint = supportCandidate.OffsetFromCharacter,
                            OffsetFromSupportToSupportPoint = supportCandidate.OffsetFromSupport,
                            SurfaceBasis = surfaceBasisQuaternion,
                            TargetVelocity = character.TargetVelocity,
                            Depth = supportCandidate.Depth,
                        };
                        if (character.Supported && !shouldRemove)
                        {
                            Simulation.Solver.ApplyDescriptionWithoutWaking(character.MotionConstraintHandle, motionConstraint);
                        }
                        else
                        {
                            ref var pendingConstraint = ref AddAndGetReference(_dynamicConstraintsToAdd);
                            pendingConstraint.Description = motionConstraint;
                            pendingConstraint.CharacterIndex = characterIndex;
                        }
                    }
                    else
                    {
                        // The character is supported by a static.
                        var motionConstraint = new StaticCharacterMotionConstraint
                        {
                            MaximumHorizontalForce = character.MaximumHorizontalForce,
                            MaximumVerticalForce = character.MaximumVerticalForce,
                            OffsetFromCharacterToSupportPoint = supportCandidate.OffsetFromCharacter,
                            SurfaceBasis = surfaceBasisQuaternion,
                            TargetVelocity = character.TargetVelocity,
                            Depth = supportCandidate.Depth,
                        };
                        if (character.Supported && !shouldRemove)
                        {
                            Simulation.Solver.ApplyDescriptionWithoutWaking(character.MotionConstraintHandle, motionConstraint);
                        }
                        else
                        {
                            ref var pendingConstraint = ref AddAndGetReference(_staticConstraintsToAdd);
                            pendingConstraint.Description = motionConstraint;
                            pendingConstraint.CharacterIndex = characterIndex;
                        }
                    }

                    character.Supported = true;
                    character.Support = supportCandidate.Support;
                }
                else
                {
                    character.Supported = false;
                }
            }

            // The TryJump flag is always reset even if the attempt failed.
            character.TryJump = false;
        }
    }

    private static ref T AddAndGetReference<T>(List<T> list)
    {
        // Sequentially add and return a stable reference for the remainder of this frame's flush.
        list.Add(default!);
        return ref CollectionsMarshal.AsSpan(list)[list.Count - 1];
    }

    /// <summary>
    ///     Updates all character support states and motion constraints based on the current character
    ///     goals and all contacts collected since the last call.
    /// </summary>
    private void AnalyzeContacts(float dt, IThreadDispatcher? threadDispatcher)
    {
        _constraintHandlesToRemove.Clear();
        _dynamicConstraintsToAdd.Clear();
        _staticConstraintsToAdd.Clear();
        _jumps.Clear();

        AnalyzeContactsForCharacterRegion(0, _characterCount, 0);

        // Flush the caches. Removals first, then additions, then jumps.
        for (var i = 0; i < _constraintHandlesToRemove.Count; ++i)
        {
            Simulation.Solver.Remove(_constraintHandlesToRemove[i]);
        }

        for (var i = 0; i < _staticConstraintsToAdd.Count; ++i)
        {
            ref var pendingConstraint = ref CollectionsMarshal.AsSpan(_staticConstraintsToAdd)[i];
            ref var character = ref _characters[pendingConstraint.CharacterIndex];
            Debug.Assert(character.Support.Mobility == CollidableMobility.Static);
            character.MotionConstraintHandle = Simulation.Solver.Add(character.BodyHandle, pendingConstraint.Description);
        }

        for (var i = 0; i < _dynamicConstraintsToAdd.Count; ++i)
        {
            ref var pendingConstraint = ref CollectionsMarshal.AsSpan(_dynamicConstraintsToAdd)[i];
            ref var character = ref _characters[pendingConstraint.CharacterIndex];
            Debug.Assert(character.Support.Mobility != CollidableMobility.Static);
            character.MotionConstraintHandle = Simulation.Solver.Add(character.BodyHandle, character.Support.BodyHandle, pendingConstraint.Description);
        }

        ref var activeSet = ref Simulation.Bodies.ActiveSet;
        for (var i = 0; i < _jumps.Count; ++i)
        {
            ref var jump = ref CollectionsMarshal.AsSpan(_jumps)[i];
            activeSet.DynamicsState[jump.CharacterBodyIndex].Motion.Velocity.Linear += jump.CharacterVelocityChange;
            if (jump.SupportBodyIndex >= 0)
            {
                BodyReference.ApplyImpulse(Simulation.Bodies.ActiveSet, jump.SupportBodyIndex,
                    jump.CharacterVelocityChange / -activeSet.DynamicsState[jump.CharacterBodyIndex].Inertia.Local.InverseMass, jump.SupportImpulseOffset);
            }
        }

        _constraintHandlesToRemove.Clear();
        _dynamicConstraintsToAdd.Clear();
        _staticConstraintsToAdd.Clear();
        _jumps.Clear();
    }

    private bool _disposed;

    /// <summary>Returns resources; unsubscribes the timestepper hooks.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Simulation.Timestepper.BeforeCollisionDetection -= PrepareForContacts;
        Simulation.Timestepper.CollisionsDetected -= AnalyzeContacts;
    }
}
