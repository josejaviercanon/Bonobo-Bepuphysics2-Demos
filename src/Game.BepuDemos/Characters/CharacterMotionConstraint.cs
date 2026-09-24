using System.Numerics;
using System.Runtime.CompilerServices;
using Bonobo.Bepuphysics2;
using Bonobo.Bepuphysics2.Constraints;
using Bonobo.BepuUtilities;
using Bonobo.BepuUtilities.Memory;
using static Bonobo.BepuUtilities.GatherScatter;

namespace Game.BepuDemos.Demos.Characters;

/// <summary>
///     Port of the upstream <c>Characters/CharacterMotionConstraint</c> (BepuPhysics2,
///     Apache-2.0, Ross Nordby). The upstream file was generated from a T4 template; this port
///     keeps the generated C# verbatim, retargeted to the Bonobo.Bepuphysics2 namespaces. The
///     static variant drives a character supported by a static; the dynamic variant drives a
///     character supported by another body.
/// </summary>
public struct CharacterMotionAccumulatedImpulse
{
    public Vector2Wide Horizontal;
    public Vector<float> Vertical;
}

/// <summary>Description of a character motion constraint where the support is static.</summary>
public struct StaticCharacterMotionConstraint : IOneBodyConstraintDescription<StaticCharacterMotionConstraint>
{
    /// <summary>Maximum force that the horizontal motion constraint can apply to reach the current velocity goal.</summary>
    public float MaximumHorizontalForce;

    /// <summary>Maximum force that the vertical motion constraint can apply to fight separation.</summary>
    public float MaximumVerticalForce;

    /// <summary>Target horizontal velocity in terms of the basis X and -Z axes.</summary>
    public Vector2 TargetVelocity;

    /// <summary>Depth of the supporting contact. The vertical motion constraint permits separating velocity if, after a frame, the objects will still be touching.</summary>
    public float Depth;

    /// <summary>
    ///     Stores the quaternion-packed orthonormal basis for the motion constraint. When expanded
    ///     into a matrix, X and Z will represent the Right and Backward directions respectively.
    ///     Y will represent Up.
    /// </summary>
    public Quaternion SurfaceBasis;

    /// <summary>World space offset from the character's center to apply impulses at.</summary>
    public Vector3 OffsetFromCharacterToSupportPoint;

    public static int ConstraintTypeId => StaticCharacterMotionTypeProcessor.BatchTypeId;

    public static Type TypeProcessorType => typeof(StaticCharacterMotionTypeProcessor);

    public static TypeProcessor CreateTypeProcessor() => new StaticCharacterMotionTypeProcessor();

    public readonly void ApplyDescription(ref TypeBatch batch, int bundleIndex, int innerIndex)
    {
        ref var target = ref GetOffsetInstance(ref Buffer<StaticCharacterMotionPrestep>.Get(ref batch.PrestepData, bundleIndex), innerIndex);
        QuaternionWide.WriteFirst(SurfaceBasis, ref target.SurfaceBasis);
        GetFirst(ref target.MaximumHorizontalForce) = MaximumHorizontalForce;
        GetFirst(ref target.MaximumVerticalForce) = MaximumVerticalForce;
        Vector2Wide.WriteFirst(TargetVelocity, ref target.TargetVelocity);
        GetFirst(ref target.Depth) = Depth;
        Vector3Wide.WriteFirst(OffsetFromCharacterToSupportPoint, ref target.OffsetFromCharacter);
    }

    public static void BuildDescription(ref TypeBatch batch, int bundleIndex, int innerIndex, out StaticCharacterMotionConstraint description)
    {
        ref var source = ref GetOffsetInstance(ref Buffer<StaticCharacterMotionPrestep>.Get(ref batch.PrestepData, bundleIndex), innerIndex);
        QuaternionWide.ReadFirst(source.SurfaceBasis, out description.SurfaceBasis);
        description.MaximumHorizontalForce = GetFirst(ref source.MaximumHorizontalForce);
        description.MaximumVerticalForce = GetFirst(ref source.MaximumVerticalForce);
        Vector2Wide.ReadFirst(source.TargetVelocity, out description.TargetVelocity);
        description.Depth = GetFirst(ref source.Depth);
        Vector3Wide.ReadFirst(source.OffsetFromCharacter, out description.OffsetFromCharacterToSupportPoint);
    }
}

/// <summary>AOSOA formatted bundle of prestep data for multiple static-supported character motion constraints.</summary>
public struct StaticCharacterMotionPrestep
{
    public QuaternionWide SurfaceBasis;
    public Vector<float> MaximumHorizontalForce;
    public Vector<float> MaximumVerticalForce;
    public Vector<float> Depth;
    public Vector2Wide TargetVelocity;
    public Vector3Wide OffsetFromCharacter;
}

public struct StaticCharacterMotionFunctions : IOneBodyConstraintFunctions<StaticCharacterMotionPrestep, CharacterMotionAccumulatedImpulse>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ComputeJacobians(
        in Vector3Wide offsetA, in QuaternionWide basisQuaternion,
        out Matrix3x3Wide basis, out Matrix2x3Wide horizontalAngularJacobianA,
        out Vector3Wide verticalAngularJacobianA)
    {
        // Both motion constraints are velocity motors (like tangent friction); no position-level goal.
        Matrix3x3Wide.CreateFromQuaternion(basisQuaternion, out basis);
        Vector3Wide.CrossWithoutOverlap(offsetA, basis.X, out horizontalAngularJacobianA.X);
        Vector3Wide.CrossWithoutOverlap(offsetA, basis.Y, out verticalAngularJacobianA);
        Vector3Wide.CrossWithoutOverlap(offsetA, basis.Z, out horizontalAngularJacobianA.Y);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyHorizontalImpulse(
        in Matrix3x3Wide basis, in Matrix2x3Wide angularJacobianA, in Vector2Wide constraintSpaceImpulse,
        in BodyInertiaWide inertiaA, ref BodyVelocityWide velocityA)
    {
        Vector3Wide.Scale(basis.X, constraintSpaceImpulse.X, out var linearImpulseAX);
        Vector3Wide.Scale(basis.Z, constraintSpaceImpulse.Y, out var linearImpulseAY);
        Vector3Wide.Add(linearImpulseAX, linearImpulseAY, out var linearImpulseA);
        Vector3Wide.Scale(linearImpulseA, inertiaA.InverseMass, out var linearChangeA);
        Vector3Wide.Add(velocityA.Linear, linearChangeA, out velocityA.Linear);

        Matrix2x3Wide.Transform(constraintSpaceImpulse, angularJacobianA, out var angularImpulseA);
        Symmetric3x3Wide.TransformWithoutOverlap(angularImpulseA, inertiaA.InverseInertiaTensor, out var angularChangeA);
        Vector3Wide.Add(velocityA.Angular, angularChangeA, out velocityA.Angular);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyVerticalImpulse(
        in Matrix3x3Wide basis, in Vector3Wide angularJacobianA, in Vector<float> constraintSpaceImpulse,
        in BodyInertiaWide inertiaA, ref BodyVelocityWide velocityA)
    {
        Vector3Wide.Scale(basis.Y, constraintSpaceImpulse, out var linearImpulseA);
        Vector3Wide.Scale(linearImpulseA, inertiaA.InverseMass, out var linearChangeA);
        Vector3Wide.Add(velocityA.Linear, linearChangeA, out velocityA.Linear);

        Vector3Wide.Scale(angularJacobianA, constraintSpaceImpulse, out var angularImpulseA);
        Symmetric3x3Wide.TransformWithoutOverlap(angularImpulseA, inertiaA.InverseInertiaTensor, out var angularChangeA);
        Vector3Wide.Add(velocityA.Angular, angularChangeA, out velocityA.Angular);
    }

    public static void WarmStart(
        in Vector3Wide positionA, in QuaternionWide orientationA, in BodyInertiaWide inertiaA,
        ref StaticCharacterMotionPrestep prestep, ref CharacterMotionAccumulatedImpulse accumulatedImpulses, ref BodyVelocityWide velocityA)
    {
        ComputeJacobians(prestep.OffsetFromCharacter, prestep.SurfaceBasis,
            out var basis, out var horizontalAngularJacobianA, out var verticalAngularJacobianA);
        ApplyHorizontalImpulse(basis, horizontalAngularJacobianA, accumulatedImpulses.Horizontal, inertiaA, ref velocityA);
        ApplyVerticalImpulse(basis, verticalAngularJacobianA, accumulatedImpulses.Vertical, inertiaA, ref velocityA);
    }

    public static void Solve(
        in Vector3Wide positionA, in QuaternionWide orientationA, in BodyInertiaWide inertiaA,
        float dt, float inverseDt, ref StaticCharacterMotionPrestep prestep, ref CharacterMotionAccumulatedImpulse accumulatedImpulses, ref BodyVelocityWide velocityA)
    {
        ComputeJacobians(prestep.OffsetFromCharacter, prestep.SurfaceBasis,
            out var basis, out var horizontalAngularJacobianA, out var verticalAngularJacobianA);

        // Compute the velocity error by projecting the body velocity into constraint space.
        Vector2Wide horizontalLinearA;
        Vector3Wide.Dot(basis.X, velocityA.Linear, out horizontalLinearA.X);
        Vector3Wide.Dot(basis.Z, velocityA.Linear, out horizontalLinearA.Y);
        Matrix2x3Wide.TransformByTransposeWithoutOverlap(velocityA.Angular, horizontalAngularJacobianA, out var horizontalAngularA);

        Vector2Wide.Add(horizontalLinearA, horizontalAngularA, out var horizontalVelocity);

        // Effective mass in constraint space.
        Symmetric3x3Wide.MatrixSandwich(horizontalAngularJacobianA, inertiaA.InverseInertiaTensor, out var inverseHorizontalEffectiveMass);

        // The linear jacobians are unit length vectors, so J * M^-1 * JT is just M^-1.
        inverseHorizontalEffectiveMass.XX += inertiaA.InverseMass;
        inverseHorizontalEffectiveMass.YY += inertiaA.InverseMass;
        Symmetric2x2Wide.InvertWithoutOverlap(inverseHorizontalEffectiveMass, out var horizontalEffectiveMass);

        Vector2Wide horizontalConstraintSpaceVelocityChange;
        horizontalConstraintSpaceVelocityChange.X = prestep.TargetVelocity.X - horizontalVelocity.X;
        // The surface basis's Z axis points opposite the view direction, so negate the Z target velocity.
        horizontalConstraintSpaceVelocityChange.Y = -prestep.TargetVelocity.Y - horizontalVelocity.Y;
        Symmetric2x2Wide.TransformWithoutOverlap(horizontalConstraintSpaceVelocityChange, horizontalEffectiveMass, out var horizontalCorrectiveImpulse);

        // Limit the force applied by the horizontal motion constraint (clamps the accumulated impulse).
        var previousHorizontalAccumulatedImpulse = accumulatedImpulses.Horizontal;
        Vector2Wide.Add(accumulatedImpulses.Horizontal, horizontalCorrectiveImpulse, out accumulatedImpulses.Horizontal);
        Vector2Wide.Length(accumulatedImpulses.Horizontal, out var horizontalImpulseMagnitude);
        var dtWide = new Vector<float>(dt);
        var maximumHorizontalImpulse = prestep.MaximumHorizontalForce * dtWide;
        var scale = Vector.Min(Vector<float>.One, maximumHorizontalImpulse / Vector.Max(new Vector<float>(1e-16f), horizontalImpulseMagnitude));
        Vector2Wide.Scale(accumulatedImpulses.Horizontal, scale, out accumulatedImpulses.Horizontal);
        Vector2Wide.Subtract(accumulatedImpulses.Horizontal, previousHorizontalAccumulatedImpulse, out horizontalCorrectiveImpulse);

        ApplyHorizontalImpulse(basis, horizontalAngularJacobianA, horizontalCorrectiveImpulse, inertiaA, ref velocityA);

        // Same for the vertical constraint.
        Vector3Wide.Dot(basis.Y, velocityA.Linear, out var verticalLinearA);
        Vector3Wide.Dot(velocityA.Angular, verticalAngularJacobianA, out var verticalAngularA);

        // If deeply penetrating, allow just enough separating velocity to reach zero depth in one frame.
        var verticalBiasVelocity = Vector.Max(Vector<float>.Zero, prestep.Depth * inverseDt);

        Symmetric3x3Wide.VectorSandwich(verticalAngularJacobianA, inertiaA.InverseInertiaTensor, out var verticalAngularContributionA);

        var inverseVerticalEffectiveMass = verticalAngularContributionA + inertiaA.InverseMass;
        var verticalCorrectiveImpulse = (verticalBiasVelocity - verticalLinearA - verticalAngularA) / inverseVerticalEffectiveMass;

        // The vertical constraint is not allowed to push, so it is also bounded at zero.
        var previousVerticalAccumulatedImpulse = accumulatedImpulses.Vertical;
        var maximumVerticalImpulse = prestep.MaximumVerticalForce * dtWide;
        accumulatedImpulses.Vertical = Vector.Min(Vector<float>.Zero, Vector.Max(accumulatedImpulses.Vertical + verticalCorrectiveImpulse, -maximumVerticalImpulse));
        verticalCorrectiveImpulse = accumulatedImpulses.Vertical - previousVerticalAccumulatedImpulse;

        ApplyVerticalImpulse(basis, verticalAngularJacobianA, verticalCorrectiveImpulse, inertiaA, ref velocityA);
    }

    public static bool RequiresIncrementalSubstepUpdates => true;

    public static void IncrementallyUpdateForSubstep(in Vector<float> dt, in BodyVelocityWide velocityA, ref StaticCharacterMotionPrestep prestep)
    {
        // Approximate the depth change by integrating the velocity along the support normal.
        Vector3Wide.CrossWithoutOverlap(velocityA.Angular, prestep.OffsetFromCharacter, out var wxra);
        Vector3Wide.Add(wxra, velocityA.Linear, out var contactVelocityA);

        var normal = QuaternionWide.TransformUnitY(prestep.SurfaceBasis);

        Vector3Wide.Dot(normal, contactVelocityA, out var estimatedDepthChangeVelocity);

        prestep.Depth -= estimatedDepthChangeVelocity * dt;
    }
}

/// <summary>Type processor for the static character motion constraint.</summary>
public class StaticCharacterMotionTypeProcessor : OneBodyTypeProcessor<StaticCharacterMotionPrestep, CharacterMotionAccumulatedImpulse, StaticCharacterMotionFunctions, AccessAll, AccessAll>
{
    /// <summary>Simulation-wide unique id for the character motion constraint.</summary>
    public const int BatchTypeId = 50;
}

/// <summary>Description of a character motion constraint where the support is dynamic.</summary>
public struct DynamicCharacterMotionConstraint : ITwoBodyConstraintDescription<DynamicCharacterMotionConstraint>
{
    /// <summary>Maximum force that the horizontal motion constraint can apply to reach the current velocity goal.</summary>
    public float MaximumHorizontalForce;

    /// <summary>Maximum force that the vertical motion constraint can apply to fight separation.</summary>
    public float MaximumVerticalForce;

    /// <summary>Target horizontal velocity in terms of the basis X and -Z axes.</summary>
    public Vector2 TargetVelocity;

    /// <summary>Depth of the supporting contact.</summary>
    public float Depth;

    /// <summary>Quaternion-packed orthonormal basis for the motion constraint (X right, Y up, Z backward).</summary>
    public Quaternion SurfaceBasis;

    /// <summary>World space offset from the character's center to apply impulses at.</summary>
    public Vector3 OffsetFromCharacterToSupportPoint;

    /// <summary>World space offset from the support's center to apply impulses at.</summary>
    public Vector3 OffsetFromSupportToSupportPoint;

    public static int ConstraintTypeId => DynamicCharacterMotionTypeProcessor.BatchTypeId;

    public static Type TypeProcessorType => typeof(DynamicCharacterMotionTypeProcessor);

    public static TypeProcessor CreateTypeProcessor() => new DynamicCharacterMotionTypeProcessor();

    public readonly void ApplyDescription(ref TypeBatch batch, int bundleIndex, int innerIndex)
    {
        ref var target = ref GetOffsetInstance(ref Buffer<DynamicCharacterMotionPrestep>.Get(ref batch.PrestepData, bundleIndex), innerIndex);
        QuaternionWide.WriteFirst(SurfaceBasis, ref target.SurfaceBasis);
        GetFirst(ref target.MaximumHorizontalForce) = MaximumHorizontalForce;
        GetFirst(ref target.MaximumVerticalForce) = MaximumVerticalForce;
        Vector2Wide.WriteFirst(TargetVelocity, ref target.TargetVelocity);
        GetFirst(ref target.Depth) = Depth;
        Vector3Wide.WriteFirst(OffsetFromCharacterToSupportPoint, ref target.OffsetFromCharacter);

        Vector3Wide.WriteFirst(OffsetFromSupportToSupportPoint, ref target.OffsetFromSupport);
    }

    public static void BuildDescription(ref TypeBatch batch, int bundleIndex, int innerIndex, out DynamicCharacterMotionConstraint description)
    {
        ref var source = ref GetOffsetInstance(ref Buffer<DynamicCharacterMotionPrestep>.Get(ref batch.PrestepData, bundleIndex), innerIndex);
        QuaternionWide.ReadFirst(source.SurfaceBasis, out description.SurfaceBasis);
        description.MaximumHorizontalForce = GetFirst(ref source.MaximumHorizontalForce);
        description.MaximumVerticalForce = GetFirst(ref source.MaximumVerticalForce);
        Vector2Wide.ReadFirst(source.TargetVelocity, out description.TargetVelocity);
        description.Depth = GetFirst(ref source.Depth);
        Vector3Wide.ReadFirst(source.OffsetFromCharacter, out description.OffsetFromCharacterToSupportPoint);

        Vector3Wide.ReadFirst(source.OffsetFromSupport, out description.OffsetFromSupportToSupportPoint);
    }
}

/// <summary>AOSOA formatted bundle of prestep data for multiple dynamic-supported character motion constraints.</summary>
public struct DynamicCharacterMotionPrestep
{
    public QuaternionWide SurfaceBasis;
    public Vector<float> MaximumHorizontalForce;
    public Vector<float> MaximumVerticalForce;
    public Vector<float> Depth;
    public Vector2Wide TargetVelocity;
    public Vector3Wide OffsetFromCharacter;

    public Vector3Wide OffsetFromSupport;
}

public struct DynamicCharacterMotionFunctions : ITwoBodyConstraintFunctions<DynamicCharacterMotionPrestep, CharacterMotionAccumulatedImpulse>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ComputeJacobians(
        in Vector3Wide offsetA, in Vector3Wide offsetB, in QuaternionWide basisQuaternion,
        out Matrix3x3Wide basis,
        out Matrix2x3Wide horizontalAngularJacobianA, out Matrix2x3Wide horizontalAngularJacobianB,
        out Vector3Wide verticalAngularJacobianA, out Vector3Wide verticalAngularJacobianB)
    {
        Matrix3x3Wide.CreateFromQuaternion(basisQuaternion, out basis);
        Vector3Wide.CrossWithoutOverlap(offsetA, basis.X, out horizontalAngularJacobianA.X);
        Vector3Wide.CrossWithoutOverlap(offsetA, basis.Y, out verticalAngularJacobianA);
        Vector3Wide.CrossWithoutOverlap(offsetA, basis.Z, out horizontalAngularJacobianA.Y);

        Vector3Wide.CrossWithoutOverlap(basis.X, offsetB, out horizontalAngularJacobianB.X);
        Vector3Wide.CrossWithoutOverlap(basis.Y, offsetB, out verticalAngularJacobianB);
        Vector3Wide.CrossWithoutOverlap(basis.Z, offsetB, out horizontalAngularJacobianB.Y);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyHorizontalImpulse(
        in Matrix3x3Wide basis,
        in Matrix2x3Wide angularJacobianA, in Matrix2x3Wide angularJacobianB, in Vector2Wide constraintSpaceImpulse,
        in BodyInertiaWide inertiaA, in BodyInertiaWide inertiaB,
        ref BodyVelocityWide velocityA, ref BodyVelocityWide velocityB)
    {
        Vector3Wide.Scale(basis.X, constraintSpaceImpulse.X, out var linearImpulseAX);
        Vector3Wide.Scale(basis.Z, constraintSpaceImpulse.Y, out var linearImpulseAY);
        Vector3Wide.Add(linearImpulseAX, linearImpulseAY, out var linearImpulseA);
        Vector3Wide.Scale(linearImpulseA, inertiaA.InverseMass, out var linearChangeA);
        Vector3Wide.Add(velocityA.Linear, linearChangeA, out velocityA.Linear);

        // Linear jacobians for B are just A's negated linear jacobians.
        Vector3Wide.Scale(linearImpulseA, inertiaB.InverseMass, out var negatedLinearChangeB);
        Vector3Wide.Subtract(velocityB.Linear, negatedLinearChangeB, out velocityB.Linear);

        Matrix2x3Wide.Transform(constraintSpaceImpulse, angularJacobianA, out var angularImpulseA);
        Symmetric3x3Wide.TransformWithoutOverlap(angularImpulseA, inertiaA.InverseInertiaTensor, out var angularChangeA);
        Vector3Wide.Add(velocityA.Angular, angularChangeA, out velocityA.Angular);

        Matrix2x3Wide.Transform(constraintSpaceImpulse, angularJacobianB, out var angularImpulseB);
        Symmetric3x3Wide.TransformWithoutOverlap(angularImpulseB, inertiaB.InverseInertiaTensor, out var angularChangeB);
        Vector3Wide.Add(velocityB.Angular, angularChangeB, out velocityB.Angular);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyVerticalImpulse(
        in Matrix3x3Wide basis,
        in Vector3Wide angularJacobianA, in Vector3Wide angularJacobianB, in Vector<float> constraintSpaceImpulse,
        in BodyInertiaWide inertiaA, in BodyInertiaWide inertiaB,
        ref BodyVelocityWide velocityA, ref BodyVelocityWide velocityB)
    {
        Vector3Wide.Scale(basis.Y, constraintSpaceImpulse, out var linearImpulseA);
        Vector3Wide.Scale(linearImpulseA, inertiaA.InverseMass, out var linearChangeA);
        Vector3Wide.Add(velocityA.Linear, linearChangeA, out velocityA.Linear);

        Vector3Wide.Scale(linearImpulseA, inertiaB.InverseMass, out var negatedLinearChangeB);
        Vector3Wide.Subtract(velocityB.Linear, negatedLinearChangeB, out velocityB.Linear);

        Vector3Wide.Scale(angularJacobianA, constraintSpaceImpulse, out var angularImpulseA);
        Symmetric3x3Wide.TransformWithoutOverlap(angularImpulseA, inertiaA.InverseInertiaTensor, out var angularChangeA);
        Vector3Wide.Add(velocityA.Angular, angularChangeA, out velocityA.Angular);

        Vector3Wide.Scale(angularJacobianB, constraintSpaceImpulse, out var angularImpulseB);
        Symmetric3x3Wide.TransformWithoutOverlap(angularImpulseB, inertiaB.InverseInertiaTensor, out var angularChangeB);
        Vector3Wide.Add(velocityB.Angular, angularChangeB, out velocityB.Angular);
    }

    public static void WarmStart(
        in Vector3Wide positionA, in QuaternionWide orientationA, in BodyInertiaWide inertiaA,
        in Vector3Wide positionB, in QuaternionWide orientationB, in BodyInertiaWide inertiaB,
        ref DynamicCharacterMotionPrestep prestep, ref CharacterMotionAccumulatedImpulse accumulatedImpulses,
        ref BodyVelocityWide velocityA, ref BodyVelocityWide velocityB)
    {
        ComputeJacobians(prestep.OffsetFromCharacter, prestep.OffsetFromSupport, prestep.SurfaceBasis,
            out var basis, out var horizontalAngularJacobianA, out var horizontalAngularJacobianB, out var verticalAngularJacobianA, out var verticalAngularJacobianB);
        ApplyHorizontalImpulse(basis, horizontalAngularJacobianA, horizontalAngularJacobianB, accumulatedImpulses.Horizontal, inertiaA, inertiaB, ref velocityA, ref velocityB);
        ApplyVerticalImpulse(basis, verticalAngularJacobianA, verticalAngularJacobianB, accumulatedImpulses.Vertical, inertiaA, inertiaB, ref velocityA, ref velocityB);
    }

    public static void Solve(
        in Vector3Wide positionA, in QuaternionWide orientationA, in BodyInertiaWide inertiaA,
        in Vector3Wide positionB, in QuaternionWide orientationB, in BodyInertiaWide inertiaB,
        float dt, float inverseDt, ref DynamicCharacterMotionPrestep prestep, ref CharacterMotionAccumulatedImpulse accumulatedImpulses,
        ref BodyVelocityWide velocityA, ref BodyVelocityWide velocityB)
    {
        ComputeJacobians(prestep.OffsetFromCharacter, prestep.OffsetFromSupport, prestep.SurfaceBasis,
            out var basis, out var horizontalAngularJacobianA, out var horizontalAngularJacobianB, out var verticalAngularJacobianA, out var verticalAngularJacobianB);

        Vector2Wide horizontalLinearA;
        Vector3Wide.Dot(basis.X, velocityA.Linear, out horizontalLinearA.X);
        Vector3Wide.Dot(basis.Z, velocityA.Linear, out horizontalLinearA.Y);
        Matrix2x3Wide.TransformByTransposeWithoutOverlap(velocityA.Angular, horizontalAngularJacobianA, out var horizontalAngularA);

        Vector2Wide negatedHorizontalLinearB;
        Vector3Wide.Dot(basis.X, velocityB.Linear, out negatedHorizontalLinearB.X);
        Vector3Wide.Dot(basis.Z, velocityB.Linear, out negatedHorizontalLinearB.Y);
        Matrix2x3Wide.TransformByTransposeWithoutOverlap(velocityB.Angular, horizontalAngularJacobianB, out var horizontalAngularB);
        Vector2Wide.Add(horizontalAngularA, horizontalAngularB, out var horizontalAngular);
        Vector2Wide.Subtract(horizontalLinearA, negatedHorizontalLinearB, out var horizontalLinear);
        Vector2Wide.Add(horizontalAngular, horizontalLinear, out var horizontalVelocity);

        Symmetric3x3Wide.MatrixSandwich(horizontalAngularJacobianA, inertiaA.InverseInertiaTensor, out var horizontalAngularContributionA);

        Symmetric3x3Wide.MatrixSandwich(horizontalAngularJacobianB, inertiaB.InverseInertiaTensor, out var horizontalAngularContributionB);
        Symmetric2x2Wide.Add(horizontalAngularContributionA, horizontalAngularContributionB, out var inverseHorizontalEffectiveMass);

        var linearContribution = inertiaA.InverseMass + inertiaB.InverseMass;

        inverseHorizontalEffectiveMass.XX += linearContribution;
        inverseHorizontalEffectiveMass.YY += linearContribution;
        Symmetric2x2Wide.InvertWithoutOverlap(inverseHorizontalEffectiveMass, out var horizontalEffectiveMass);

        Vector2Wide horizontalConstraintSpaceVelocityChange;
        horizontalConstraintSpaceVelocityChange.X = prestep.TargetVelocity.X - horizontalVelocity.X;
        // The surface basis's Z axis points opposite the view direction, so negate the Z target velocity.
        horizontalConstraintSpaceVelocityChange.Y = -prestep.TargetVelocity.Y - horizontalVelocity.Y;
        Symmetric2x2Wide.TransformWithoutOverlap(horizontalConstraintSpaceVelocityChange, horizontalEffectiveMass, out var horizontalCorrectiveImpulse);

        var previousHorizontalAccumulatedImpulse = accumulatedImpulses.Horizontal;
        Vector2Wide.Add(accumulatedImpulses.Horizontal, horizontalCorrectiveImpulse, out accumulatedImpulses.Horizontal);
        Vector2Wide.Length(accumulatedImpulses.Horizontal, out var horizontalImpulseMagnitude);
        var dtWide = new Vector<float>(dt);
        var maximumHorizontalImpulse = prestep.MaximumHorizontalForce * dtWide;
        var scale = Vector.Min(Vector<float>.One, maximumHorizontalImpulse / Vector.Max(new Vector<float>(1e-16f), horizontalImpulseMagnitude));
        Vector2Wide.Scale(accumulatedImpulses.Horizontal, scale, out accumulatedImpulses.Horizontal);
        Vector2Wide.Subtract(accumulatedImpulses.Horizontal, previousHorizontalAccumulatedImpulse, out horizontalCorrectiveImpulse);

        ApplyHorizontalImpulse(basis, horizontalAngularJacobianA, horizontalAngularJacobianB, horizontalCorrectiveImpulse, inertiaA, inertiaB, ref velocityA, ref velocityB);

        Vector3Wide.Dot(basis.Y, velocityA.Linear, out var verticalLinearA);
        Vector3Wide.Dot(velocityA.Angular, verticalAngularJacobianA, out var verticalAngularA);

        Vector3Wide.Dot(basis.Y, velocityB.Linear, out var negatedVerticalLinearB);
        Vector3Wide.Dot(velocityB.Angular, verticalAngularJacobianB, out var verticalAngularB);

        var verticalBiasVelocity = Vector.Max(Vector<float>.Zero, prestep.Depth * inverseDt);

        Symmetric3x3Wide.VectorSandwich(verticalAngularJacobianA, inertiaA.InverseInertiaTensor, out var verticalAngularContributionA);

        Symmetric3x3Wide.VectorSandwich(verticalAngularJacobianB, inertiaB.InverseInertiaTensor, out var verticalAngularContributionB);

        var inverseVerticalEffectiveMass = verticalAngularContributionA + verticalAngularContributionB + linearContribution;
        var verticalCorrectiveImpulse = (verticalBiasVelocity - verticalLinearA + negatedVerticalLinearB - verticalAngularA - verticalAngularB) / inverseVerticalEffectiveMass;

        var previousVerticalAccumulatedImpulse = accumulatedImpulses.Vertical;
        var maximumVerticalImpulse = prestep.MaximumVerticalForce * dtWide;
        accumulatedImpulses.Vertical = Vector.Min(Vector<float>.Zero, Vector.Max(accumulatedImpulses.Vertical + verticalCorrectiveImpulse, -maximumVerticalImpulse));
        verticalCorrectiveImpulse = accumulatedImpulses.Vertical - previousVerticalAccumulatedImpulse;

        ApplyVerticalImpulse(basis, verticalAngularJacobianA, verticalAngularJacobianB, verticalCorrectiveImpulse, inertiaA, inertiaB, ref velocityA, ref velocityB);
    }

    public static bool RequiresIncrementalSubstepUpdates => true;

    public static void IncrementallyUpdateForSubstep(in Vector<float> dt, in BodyVelocityWide velocityA, in BodyVelocityWide velocityB, ref DynamicCharacterMotionPrestep prestep)
    {
        Vector3Wide.CrossWithoutOverlap(velocityA.Angular, prestep.OffsetFromCharacter, out var wxra);
        Vector3Wide.Add(wxra, velocityA.Linear, out var contactVelocityA);

        var normal = QuaternionWide.TransformUnitY(prestep.SurfaceBasis);

        Vector3Wide.CrossWithoutOverlap(velocityB.Angular, prestep.OffsetFromSupport, out var wxrb);
        Vector3Wide.Add(wxrb, velocityB.Linear, out var contactVelocityB);
        Vector3Wide.Subtract(contactVelocityA, contactVelocityB, out var contactVelocityDifference);
        Vector3Wide.Dot(normal, contactVelocityDifference, out var estimatedDepthChangeVelocity);

        prestep.Depth -= estimatedDepthChangeVelocity * dt;
    }
}

/// <summary>Type processor for the dynamic character motion constraint.</summary>
public class DynamicCharacterMotionTypeProcessor : TwoBodyTypeProcessor<DynamicCharacterMotionPrestep, CharacterMotionAccumulatedImpulse, DynamicCharacterMotionFunctions, AccessAll, AccessAll, AccessAll, AccessAll>
{
    /// <summary>Simulation-wide unique id for the character motion constraint.</summary>
    public const int BatchTypeId = 51;
}
