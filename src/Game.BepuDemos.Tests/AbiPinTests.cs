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
        Assert.Equal(6, SignalBuffer.HeaderLength);
        Assert.Equal(0, SignalBuffer.HeaderSeq);
        Assert.Equal(1, SignalBuffer.HeaderEpoch);
        Assert.Equal(2, SignalBuffer.HeaderEntityCount);
        Assert.Equal(3, SignalBuffer.HeaderStride);
        Assert.Equal(4, SignalBuffer.HeaderStepMs);
        Assert.Equal(5, SignalBuffer.HeaderTickMs);
    }

    [Fact]
    public void Transform3DLayout_IsStride12Float64()
    {
        Assert.Equal(12, SignalBufferLayout.Transform3DStride);
        Assert.Equal(8, SignalBufferLayout.Transform3DScalarSize);
        Assert.Equal(96, SignalBufferLayout.Transform3DByteLength);
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
        Assert.Equal(7d, buffer[SignalBuffer.HeaderLength]);         // id
        Assert.Equal(1d, buffer[SignalBuffer.HeaderLength + 1]);     // x
        Assert.Equal(1d, buffer[SignalBuffer.HeaderLength + 7]);     // qw
        Assert.Equal(EntityLifecycle3.Active, buffer[SignalBuffer.HeaderLength + 11]);
    }
}
