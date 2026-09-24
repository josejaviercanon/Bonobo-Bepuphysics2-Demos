using System.Diagnostics;
using System.Numerics;
using Bonobo.Bepuphysics2;
using Bonobo.Bepuphysics2.Collidables;
using Bonobo.BepuUtilities;

namespace Game.BepuDemos.Demos.Characters;

/// <summary>
///     Convenience wrapper around a character controller reference and its body (upstream
///     <c>CharacterInput</c>, BepuPhysics2, Apache-2.0, Ross Nordby). The upstream OpenTK
///     keyboard/camera polling is replaced by the test bed's pinned input ring: the demo passes
///     an already camera-relative movement direction plus jump/sprint buttons.
/// </summary>
public struct CharacterInput
{
    private BodyHandle _bodyHandle;
    private CharacterControllers _characters;
    private float _speed;
    private Capsule _shape;

    public BodyHandle BodyHandle => _bodyHandle;

    public CharacterInput(
        CharacterControllers characters, Vector3 initialPosition, Capsule shape,
        float minimumSpeculativeMargin, float mass, float maximumHorizontalForce, float maximumVerticalGlueForce,
        float jumpVelocity, float speed, float maximumSlope = MathF.PI * 0.25f)
    {
        _characters = characters;
        var shapeIndex = characters.Simulation.Shapes.Add(shape);

        // Characters are dynamic, so they require a defined BodyInertia. The inverse inertia
        // tensor is left at its default all-zeroes value (infinite inertia: no rotation).
        // Deviation (docs/compat-review.md): the body uses a negative deactivation threshold
        // (never sleeps) because test-bed input arrives one step behind the goals; a sleep
        // transition in the same step as a goal change would otherwise swallow the input.
        _bodyHandle = characters.Simulation.Bodies.Add(BodyDescription.CreateDynamic(
            initialPosition, new BodyInertia { InverseMass = 1f / mass },
            new CollidableDescription(shapeIndex, minimumSpeculativeMargin, float.MaxValue, ContinuousDetection.Passive),
            -1f));
        ref var character = ref characters.AllocateCharacter(_bodyHandle);
        character.LocalUp = new Vector3(0, 1, 0);
        character.CosMaximumSlope = MathF.Cos(maximumSlope);
        character.JumpVelocity = jumpVelocity;
        character.MaximumVerticalForce = maximumVerticalGlueForce;
        character.MaximumHorizontalForce = maximumHorizontalForce;
        character.MinimumSupportDepth = shape.Radius * -0.01f;
        character.MinimumSupportContinuationDepth = -minimumSpeculativeMargin;
        _speed = speed;
        _shape = shape;
    }

    /// <summary>
    ///     Sets the character's movement goals for the next step from a camera-relative movement
    ///     direction (magnitude 0..1), jump/sprint buttons and the fixed timestep duration.
    /// </summary>
    public void UpdateCharacterGoals(Vector2 movementDirection, bool tryJump, bool sprint, float simulationTimestepDuration)
    {
        var movementDirectionLengthSquared = movementDirection.LengthSquared();
        var movementLength = 0f;
        if (movementDirectionLengthSquared > 0)
        {
            movementLength = MathF.Sqrt(movementDirectionLengthSquared);
            movementDirection /= movementLength;
            // The input direction is the desired facing, clamped to unit length.
            if (movementLength > 1f) movementLength = 1f;
        }

        ref var character = ref _characters.GetCharacterByBodyHandle(_bodyHandle);
        character.TryJump = tryJump;
        var characterBody = new BodyReference(_bodyHandle, _characters.Simulation.Bodies);
        var effectiveSpeed = sprint ? _speed * 1.75f : _speed;
        // TargetVelocity.Y is the desired velocity along the view direction (forward), so the
        // world-space input direction maps to a pure forward goal after facing it.
        var newTargetVelocity = new Vector2(0f, movementLength * effectiveSpeed);
        // The view direction drives the surface basis (and air control); follow the movement input.
        var viewDirection = movementDirectionLengthSquared > 0
            ? Vector3.Normalize(new Vector3(movementDirection.X, 0f, movementDirection.Y))
            : character.ViewDirection;

        // Modifying the character's raw data does not wake it; do so explicitly when goals changed.
        if (!characterBody.Awake &&
            ((character.TryJump && character.Supported) ||
             newTargetVelocity != character.TargetVelocity ||
             (newTargetVelocity != Vector2.Zero && character.ViewDirection != viewDirection)))
        {
            _characters.Simulation.Awakener.AwakenBody(character.BodyHandle);
        }

        character.TargetVelocity = newTargetVelocity;
        character.ViewDirection = viewDirection;

        // The motion constraints aren't active in the air, so air control is applied manually.
        if (!character.Supported && movementDirectionLengthSquared > 0)
        {
            QuaternionEx.Transform(character.LocalUp, characterBody.Pose.Orientation, out var characterUp);
            var characterRight = Vector3.Cross(character.ViewDirection, characterUp);
            var rightLengthSquared = characterRight.LengthSquared();
            if (rightLengthSquared > 1e-10f)
            {
                characterRight /= MathF.Sqrt(rightLengthSquared);
                var characterForward = Vector3.Cross(characterUp, characterRight);
                var worldMovementDirection = characterRight * (movementDirection.X * movementLength) + characterForward * (movementDirection.Y * movementLength);
                var currentVelocity = Vector3.Dot(characterBody.Velocity.Linear, worldMovementDirection);
                // Air control is arbitrarily a fraction of supported movement's speed/force.
                const float airControlForceScale = 0.2f;
                const float airControlSpeedScale = 0.2f;
                var airAccelerationDt = characterBody.LocalInertia.InverseMass * character.MaximumHorizontalForce * airControlForceScale * simulationTimestepDuration;
                var maximumAirSpeed = effectiveSpeed * airControlSpeedScale;
                var targetVelocity = MathF.Min(currentVelocity + airAccelerationDt, maximumAirSpeed);
                // Never slow down movement along the desired direction.
                var velocityChangeAlongMovementDirection = MathF.Max(0, targetVelocity - currentVelocity);
                characterBody.Velocity.Linear += worldMovementDirection * velocityChangeAlongMovementDirection;
                Debug.Assert(characterBody.Awake, "Velocity changes don't automatically update objects; the character should have already been woken up before applying air control.");
            }
        }
    }

    /// <summary>Removes the character's body from the simulation and the character from the set.</summary>
    public void Dispose()
    {
        _characters.Simulation.Shapes.Remove(new BodyReference(_bodyHandle, _characters.Simulation.Bodies).Collidable.Shape);
        _characters.Simulation.Bodies.Remove(_bodyHandle);
        _characters.RemoveCharacterByBodyHandle(_bodyHandle);
    }
}
