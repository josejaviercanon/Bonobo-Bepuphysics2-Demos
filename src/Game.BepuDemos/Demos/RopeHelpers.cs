using System.Numerics;
using Bonobo.Bepuphysics2;
using Bonobo.Bepuphysics2.Collidables;
using Bonobo.Bepuphysics2.Constraints;
using Bonobo.BepuUtilities;

namespace Game.BepuDemos.Demos;

/// <summary>
///     Rope construction helpers ported from the upstream <c>RopeStabilityDemo</c>
///     (BepuPhysics2, Apache-2.0, Ross Nordby): a chain of sphere bodies joined by
///     <see cref="DistanceLimit"/> constraints with a kinematic top link, plus the wrecking-ball
///     attachment. Shared by <see cref="SubsteppingDemo"/> and the P2b rope demos.
/// </summary>
public static class RopeHelpers
{
    public static BodyHandle[] BuildRopeBodies(
        Simulation simulation, Vector3 start, int bodyCount, float bodySize, float bodySpacing,
        float massPerBody, float inverseInertiaScale)
    {
        var handles = new BodyHandle[bodyCount + 1];
        var ropeShape = new Sphere(bodySize);
        var ropeInertia = ropeShape.ComputeInertia(massPerBody);
        Symmetric3x3.Scale(ropeInertia.InverseInertiaTensor, inverseInertiaScale, out ropeInertia.InverseInertiaTensor);
        var ropeShapeIndex = simulation.Shapes.Add(ropeShape);

        var bodyDescription = new BodyDescription
        {
            Activity = .01f,
            Collidable = ropeShapeIndex,
        };
        for (var linkIndex = 0; linkIndex < bodyCount + 1; ++linkIndex)
        {
            // The uppermost body is kinematic to hold up the rest of the chain.
            bodyDescription.LocalInertia = linkIndex == 0 ? new BodyInertia() : ropeInertia;
            bodyDescription.Pose = start - new Vector3(0, linkIndex * (bodySpacing + 2 * bodySize), 0);
            handles[linkIndex] = simulation.Bodies.Add(bodyDescription);
        }

        return handles;
    }

    public static BodyHandle[] BuildRope(
        Simulation simulation, Vector3 start, int bodyCount, float bodySize, float bodySpacing,
        float constraintOffsetLength, float massPerBody, float inverseInertiaScale, SpringSettings springSettings)
    {
        var handles = BuildRopeBodies(simulation, start, bodyCount, bodySize, bodySpacing, massPerBody, inverseInertiaScale);
        var maximumDistance = 2 * bodySize + bodySpacing - 2 * constraintOffsetLength;
        for (var i = 0; i < handles.Length - 1; ++i)
        {
            simulation.Solver.Add(handles[i], handles[i + 1], new DistanceLimit(
                new Vector3(0, -constraintOffsetLength, 0),
                new Vector3(0, constraintOffsetLength, 0),
                maximumDistance * 0.1f,
                maximumDistance,
                springSettings));
        }

        return handles;
    }

    public static BodyHandle CreateWreckingBall(
        Simulation simulation, BodyHandle[] bodyHandles, float ropeBodyRadius, float bodySpacing,
        float wreckingBallRadius, BodyInertia wreckingBallInertia, TypedIndex wreckingBallShapeIndex)
    {
        var lastBodyPose = simulation.Bodies[bodyHandles[^1]].Pose;
        var wreckingBallPosition = lastBodyPose.Position - new Vector3(0, ropeBodyRadius + bodySpacing + wreckingBallRadius, 0);
        var description = BodyDescription.CreateDynamic(wreckingBallPosition, wreckingBallInertia, wreckingBallShapeIndex, 0.01f);
        // Give it a little bump.
        description.Velocity = new Vector3(-10, 0, 0);
        return simulation.Bodies.Add(description);
    }

    public static BodyHandle AttachWreckingBall(
        Simulation simulation, BodyHandle[] bodyHandles, float ropeBodyRadius, float bodySpacing,
        float constraintOffsetLength, float wreckingBallRadius, BodyInertia wreckingBallInertia,
        TypedIndex wreckingBallShapeIndex, SpringSettings springSettings)
    {
        var wreckingBallBodyHandle = CreateWreckingBall(
            simulation, bodyHandles, ropeBodyRadius, bodySpacing, wreckingBallRadius, wreckingBallInertia, wreckingBallShapeIndex);
        var maximumDistance = bodySpacing + ropeBodyRadius - constraintOffsetLength;
        simulation.Solver.Add(bodyHandles[^1], wreckingBallBodyHandle, new DistanceLimit(
            new Vector3(0, -constraintOffsetLength, 0),
            new Vector3(0, wreckingBallRadius, 0),
            maximumDistance * 0.1f,
            maximumDistance,
            springSettings));
        return wreckingBallBodyHandle;
    }
}
