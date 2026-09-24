using DemoEngine.ECS;
using DemoEngine.Inputs;
using Xunit;

namespace Game.BepuDemos.Tests;

/// <summary>
///     Pins the shared-memory ABI mirrored from the Bonobo engine. A change here is a
///     compatibility break with the engine (and with the Babylon.js decoders in
///     <c>src/BepuDemos.UI</c>) and must be reviewed — see docs/compat-review.md.
/// </summary>
public class AbiPinTests
{
    [Fact]
    public void SignalHeader_HasEngineShape()
    {
        Assert.Equal(8, SignalBuffer.HeaderLength);
        Assert.Equal(0, SignalBuffer.HeaderSeq);
        Assert.Equal(1, SignalBuffer.HeaderEpoch);
        Assert.Equal(2, SignalBuffer.HeaderEntityCount);
        Assert.Equal(3, SignalBuffer.HeaderStride);
        Assert.Equal(4, SignalBuffer.HeaderStepMs);
        Assert.Equal(5, SignalBuffer.HeaderTickMs);
        Assert.Equal(6, SignalBuffer.HeaderLineCount);
        Assert.Equal(7, SignalBuffer.HeaderLineStride);
    }

    [Fact]
    public void Transform3DLayout_IsStride12Float64()
    {
        Assert.Equal(12, SignalBufferLayout.Transform3DStride);
        Assert.Equal(8, SignalBufferLayout.Transform3DScalarSize);
        Assert.Equal(96, SignalBufferLayout.Transform3DByteLength);
    }

    [Fact]
    public void LineStateLayout_IsStride12Float64()
    {
        Assert.Equal(12, SignalBufferLayout.LineStateStride);
        Assert.Equal(8, SignalBufferLayout.LineStateScalarSize);
        Assert.Equal(96, SignalBufferLayout.LineStateByteLength);
    }

    [Fact]
    public void GlobalClock_IsEightFloat64Elements()
    {
        Assert.Equal(8, SignalBuffer.GlobalClockLength);
        Assert.Equal(8, SignalBufferLayout.GlobalClockStride);
        Assert.Equal(8, SignalBufferLayout.GlobalClockScalarSize);
        Assert.Equal(64, SignalBufferLayout.GlobalClockByteLength);
        Assert.Equal(6, SignalBuffer.GlobalClockReserved0);
        Assert.Equal(7, SignalBuffer.GlobalClockReserved1);
    }

    [Fact]
    public void InputRing_IsEightSlotsByHundredRecords()
    {
        Assert.Equal(8, InputRingLayout.SlotSize);
        Assert.Equal(100, InputRingLayout.QueueCapacity);
    }

    [Fact]
    public void InputPacketIds_MatchEngineValues()
    {
        Assert.Equal(1d, InputPacketIds.ClickMove);
        Assert.Equal(2d, InputPacketIds.FireBall);
        Assert.Equal(3d, InputPacketIds.SceneLoaded);
        Assert.Equal(4d, InputPacketIds.CharacterMove);
        Assert.Equal(5d, InputPacketIds.VehicleControl);
        Assert.Equal(6d, InputPacketIds.TankControl);
    }

    private sealed class PacketRecorder : ICharacterMoveSink, IVehicleControlSink, ITankControlSink
    {
        public CharacterMoveInput CharacterMove;
        public VehicleControlInput VehicleControl;
        public TankControlInput TankControl;

        public void OnCharacterMove(in CharacterMoveInput input) => CharacterMove = input;

        public void OnVehicleControl(in VehicleControlInput input) => VehicleControl = input;

        public void OnTankControl(in TankControlInput input) => TankControl = input;
    }

    [Fact]
    public void InputDispatcher_RoutesP3PlayerIntents()
    {
        var recorder = new PacketRecorder();

        Assert.True(InputDispatcher.Dispatch(new[] { 4d, 0.5d, -0.25d, 1d, 0d, 0d, 0d, 0d }, recorder));
        Assert.Equal(0.5d, recorder.CharacterMove.MoveX);
        Assert.Equal(-0.25d, recorder.CharacterMove.MoveZ);
        Assert.Equal(1d, recorder.CharacterMove.Jump);
        Assert.Equal(0d, recorder.CharacterMove.Sprint);

        Assert.True(InputDispatcher.Dispatch(new[] { 5d, -1d, 0.75d, 1d, 0d, 0d, 0d, 0d }, recorder));
        Assert.Equal(-1d, recorder.VehicleControl.Throttle);
        Assert.Equal(0.75d, recorder.VehicleControl.Steer);
        Assert.Equal(1d, recorder.VehicleControl.Zoom);
        Assert.Equal(0d, recorder.VehicleControl.Brake);

        Assert.True(InputDispatcher.Dispatch(new[] { 6d, 1d, -1d, 0.5d, -0.5d, 1d, 1d, 0d }, recorder));
        Assert.Equal(1d, recorder.TankControl.Move);
        Assert.Equal(-1d, recorder.TankControl.Turn);
        Assert.Equal(0.5d, recorder.TankControl.AimHorizontal);
        Assert.Equal(-0.5d, recorder.TankControl.AimVertical);
        Assert.Equal(1d, recorder.TankControl.Fire);
        Assert.Equal(1d, recorder.TankControl.Zoom);
        Assert.Equal(0d, recorder.TankControl.Brake);

        Assert.False(InputDispatcher.Dispatch(new[] { 99d, 0d, 0d, 0d, 0d, 0d, 0d, 0d }, recorder));
    }

    [Fact]
    public void Transform3DEncoder_WritesHeaderAndRecords()
    {
        var states = new List<Transform3DState>
        {
            new(7, 1d, 2d, 3d, 0d, 0d, 0d, 1d, 2d, 2d, 2d, EntityLifecycle3.Active),
        };
        var signal = new Transform3DRenderSignal(5, states.Count, 1.25d, states);

        var length = SignalBufferEncoders.ElementLength(signal);
        var buffer = new double[length];
        SignalBufferEncoders.Encode(signal, buffer);

        Assert.Equal(SignalBuffer.HeaderLength + 12, length);
        Assert.Equal(5d, buffer[SignalBuffer.HeaderSeq]);
        Assert.Equal(1d, buffer[SignalBuffer.HeaderEntityCount]);
        Assert.Equal(12d, buffer[SignalBuffer.HeaderStride]);
        Assert.Equal(0d, buffer[SignalBuffer.HeaderLineCount]);
        Assert.Equal(SignalBufferLayout.LineStateStride, buffer[SignalBuffer.HeaderLineStride]);
        Assert.Equal(7d, buffer[SignalBuffer.HeaderLength]);         // id
        Assert.Equal(1d, buffer[SignalBuffer.HeaderLength + 1]);     // x
        Assert.Equal(1d, buffer[SignalBuffer.HeaderLength + 7]);     // qw
        Assert.Equal(EntityLifecycle3.Active, buffer[SignalBuffer.HeaderLength + 11]);
    }

    [Fact]
    public void LineEncoder_WritesLineRegionAfterTransforms()
    {
        var states = new List<Transform3DState>
        {
            new(7, 1d, 2d, 3d, 0d, 0d, 0d, 1d, 2d, 2d, 2d, EntityLifecycle3.Active),
        };
        var lines = new List<LineState>
        {
            new(1000, 1d, 2d, 3d, 4d, 5d, 6d, 1d, 0d, 0d, 1d),
            new(1001, 7d, 8d, 9d, 10d, 11d, 12d, 0d, 1d, 0d, 1d),
        };
        var signal = new Transform3DRenderSignal(9, states.Count, 0.5d, states, lines);

        var length = SignalBufferEncoders.ElementLength(signal);
        var buffer = new double[length];
        SignalBufferEncoders.Encode(signal, buffer);

        Assert.Equal(SignalBuffer.HeaderLength + 12 + 24, length);
        Assert.Equal(2d, buffer[SignalBuffer.HeaderLineCount]);
        Assert.Equal(12d, buffer[SignalBuffer.HeaderLineStride]);

        var lineBase = SignalBuffer.HeaderLength + 12;
        Assert.Equal(1000d, buffer[lineBase]);            // id
        Assert.Equal(1d, buffer[lineBase + 1]);           // ax
        Assert.Equal(6d, buffer[lineBase + 6]);           // bz
        Assert.Equal(1d, buffer[lineBase + 7]);           // r
        Assert.Equal(1d, buffer[lineBase + 10]);          // a
        Assert.Equal(0d, buffer[lineBase + 11]);          // reserved
        Assert.Equal(1001d, buffer[lineBase + 12]);       // second line id
    }
}
