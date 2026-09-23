namespace DemoEngine.ECS;

/// <summary>
///     Transport seam between a simulation and its presentation layer. Mirrors
///     <c>Game.Engine.ECS.IRenderTransport&lt;TSignal&gt;</c>: the simulation pushes batched
///     render signals and the implementation decides how they reach the consumer.
/// </summary>
/// <typeparam name="TSignal">Batched render-signal record emitted by the simulation.</typeparam>
public interface IRenderTransport<TSignal>
{
    /// <summary>Raised for every pushed signal (test probes subscribe here).</summary>
    event Action<TSignal>? OnSignal;

    /// <summary>Pushes one batched render signal toward the presentation side.</summary>
    void Push(TSignal signal);
}

/// <summary>In-process transport for unit tests: captures the latest pushed signal.</summary>
public sealed class CapturingRenderTransport<TSignal> : IRenderTransport<TSignal>
{
    public event Action<TSignal>? OnSignal;

    /// <summary>Every signal pushed so far, in order.</summary>
    public List<TSignal> Signals { get; } = new();

    public TSignal? Last { get; private set; }

    public void Push(TSignal signal)
    {
        Signals.Add(signal);
        Last = signal;
        OnSignal?.Invoke(signal);
    }
}

/// <summary>
///     Co-located render path: encodes the signal into a pinned buffer and notifies the host,
///     which exposes the memory directly to the presentation layer (WebView2 shared buffer on
///     this host). No JSON, no per-entity interop calls. Mirrors
///     <c>Game.Engine.ECS.DirectRenderTransport&lt;TSignal, T&gt;</c>.
/// </summary>
public sealed class DirectRenderTransport<TSignal, T> : IRenderTransport<TSignal>
    where T : unmanaged
{
    private readonly string _eventName;
    private readonly Func<TSignal, int> _elementLength;
    private readonly Action<TSignal, Span<T>> _encode;
    private readonly PinnedRenderBuffer<T> _buffer;

    public DirectRenderTransport(
        string eventName,
        Func<TSignal, int> elementLength,
        Action<TSignal, Span<T>> encode,
        PinnedRenderBuffer<T> buffer)
    {
        _eventName = eventName;
        _elementLength = elementLength;
        _encode = encode;
        _buffer = buffer;
    }

    public event Action<TSignal>? OnSignal;

    public void Push(TSignal signal)
    {
        OnSignal?.Invoke(signal);
        var elementCount = _elementLength(signal);
        var span = _buffer.GetSpan(elementCount);
        _encode(signal, span);
        _buffer.Commit(_eventName);
    }
}
