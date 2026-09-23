using DemoEngine.Config;
using DemoEngine.Inputs;
using DemoEngine.Simulations;

namespace DemoEngine.ECS;

/// <summary>
///     Host-agnostic simulation control. Mirrors <c>Game.Engine.ECS.SimulationHost</c> for the
///     subset this test bed needs:
///       * owns the registered demo modules (<c>Connect(gameKey)</c> creates the module's
///         simulation + pinned signal buffer; unknown keys leave the engine idle);
///       * owns the pinned input ring and drains it at the top of every <see cref="Tick"/>
///         through the hand-written <see cref="InputDispatcher"/>, forwarding decoded packets
///         to the active module when it implements the sink interfaces;
///       * owns the host-scope "globals" clock block (fixed 8-element buffer, republished on
///         every <see cref="Tick"/>/<see cref="SetPaused"/>/<see cref="Connect"/>);
///       * runs the 1/60 s fixed-step accumulator (<see cref="Tick(double)"/>); simulations
///         expose <c>Step(dt)</c> and own no timers.
///     The WinApp host forwards committed buffers to its WebView2 shared-buffer channels via
///     <see cref="BufferNotify"/>.
/// </summary>
public sealed class SimulationHost : IDisposable
{
    /// <summary>Committed-buffer callback: (signal name, pinned pointer, scalar element count).</summary>
    public delegate void BufferNotify(string eventName, nint bufferPtr, int elementCount);

    public const double FixedStepSeconds = 1.0 / 60.0;
    public const int MaxStepsPerTick = 4;
    public const double MaxTickDeltaSeconds = 0.25;

    public const string GlobalsEventName = "globals";
    public const int GlobalClockCapacity = 8;

    private readonly BufferNotify _notify;
    private readonly Dictionary<string, DemoModuleEntry> _modules = new();
    private readonly object _sync = new();

    private readonly PinnedRenderBuffer<double> _globalsBuffer;
    private readonly PinnedRenderBuffer<double> _inputBuffer;
    private readonly PinnedRenderBuffer<int> _inputHeadBuffer;

    private DemoWorldConfig? _worldConfig = DemoWorldConfig.Default;
    private DemoModuleEntry? _activeModule;
    private IDemoSimulation? _activeSim;
    private PinnedRenderBuffer<double>? _activeBuffer;
    private string? _activeGame;

    private double _accumulator;
    private double _simulationTimeSeconds;
    private double _lastStepSeconds;
    private long _stepCount;
    private bool _paused;
    private int _inputTail;
    private long _processedInputs;
    private long _droppedInputs;

    public SimulationHost(BufferNotify notify)
        : this(notify, null)
    {
    }

    public SimulationHost(BufferNotify notify, DemoRegistry? modules)
    {
        _notify = notify ?? throw new ArgumentNullException(nameof(notify));

        if (modules is not null)
        {
            foreach (var module in modules.Modules)
                _modules.Add(module.GameKey, module);
        }

        _globalsBuffer = CreateBuffer(GlobalsEventName, GlobalClockCapacity);
        _inputBuffer = new PinnedRenderBuffer<double>(InputRingLayout.QueueCapacity * InputRingLayout.SlotSize);
        _inputHeadBuffer = new PinnedRenderBuffer<int>(1);

        PublishGlobalClockLocked();
    }

    /// <summary>Round physics pause state: while paused, <see cref="Tick"/> executes no steps.</summary>
    public bool IsPaused
    {
        get
        {
            lock (_sync) return _paused;
        }
    }

    /// <summary>Game keys registered by the host's modules, in registration order.</summary>
    public IReadOnlyCollection<string> GameKeys
    {
        get
        {
            lock (_sync) return _modules.Keys.ToArray();
        }
    }

    /// <summary>Test/diagnostic probe: the currently connected simulation, if any.</summary>
    internal IDemoSimulation? ActiveSimulation
    {
        get
        {
            lock (_sync) return _activeSim;
        }
    }

    /// <summary>Test/diagnostic probe: input records routed into the simulation since process start.</summary>
    internal long ProcessedInputCount
    {
        get
        {
            lock (_sync) return _processedInputs;
        }
    }

    /// <summary>Test/diagnostic probe: input records dropped since process start.</summary>
    internal long DroppedInputCount
    {
        get
        {
            lock (_sync) return _droppedInputs;
        }
    }

    /// <summary>Test/diagnostic probe: per-connect pinned signal buffers released on stop.</summary>
    internal int DisposedSignalBufferCount { get; private set; }

    /// <summary>Payload-free module verb routing (<c>/api/{game}/{verb}</c> from the page).</summary>
    public bool SendCommand(string game, string verb)
    {
        lock (_sync)
        {
            if (_activeGame != game || _activeSim is not IDemoCommands commands) return false;
            return commands.TryCommand(verb);
        }
    }

    /// <summary>Stable pinned address + capacity of a registered signal buffer (host handshake).</summary>
    public bool TryGetSignalInfo(string eventName, out nint pointer, out int capacity)
    {
        lock (_sync)
        {
            if (eventName == GlobalsEventName)
            {
                pointer = _globalsBuffer.Ptr;
                capacity = _globalsBuffer.Capacity;
                return true;
            }

            pointer = 0;
            capacity = 0;
            return false;
        }
    }

    /// <summary>Stable pinned addresses of the input ring (data + head counter).</summary>
    public bool TryGetInputInfo(out nint dataPtr, out nint headPtr, out int capacity)
    {
        lock (_sync)
        {
            dataPtr = _inputBuffer.Ptr;
            headPtr = _inputHeadBuffer.Ptr;
            capacity = InputRingLayout.QueueCapacity;
            return true;
        }
    }

    /// <summary>
    ///     Routes one decoded 8-double record (WebView2 ReadWrite shared input mapping path).
    ///     The record is dispatched to the active simulation through the hand-written
    ///     dispatcher; unhandled records count as dropped.
    /// </summary>
    public void ProcessInputRecord(ReadOnlySpan<double> record)
    {
        lock (_sync)
        {
            if (InputDispatcher.Dispatch(record, _activeSim))
                _processedInputs++;
            else
                _droppedInputs++;
        }
    }

    /// <summary>
    ///     Host-pumped fixed-step accumulator: drains the pinned input ring, advances the active
    ///     module's simulation in <see cref="FixedStepSeconds"/> steps (catch-up capped at
    ///     <see cref="MaxStepsPerTick"/>) and republishes the globals clock. Returns the number
    ///     of simulation steps executed.
    /// </summary>
    public int Tick(double deltaSeconds)
    {
        var steps = 0;

        lock (_sync)
        {
            DrainPinnedInputsLocked();

            if (!_paused && _activeSim is not null)
            {
                var delta = Math.Min(Math.Max(deltaSeconds, 0d), MaxTickDeltaSeconds);
                _accumulator += delta;

                while (_accumulator >= FixedStepSeconds && steps < MaxStepsPerTick)
                {
                    _activeSim.Step(FixedStepSeconds);
                    _accumulator -= FixedStepSeconds;
                    _simulationTimeSeconds += FixedStepSeconds;
                    _lastStepSeconds = FixedStepSeconds;
                    _stepCount++;
                    steps++;
                }
            }

            PublishGlobalClockLocked();
        }

        return steps;
    }

    /// <summary>Pause/resume; the accumulator keeps its residual and the clock block is republished.</summary>
    public void SetPaused(bool paused)
    {
        lock (_sync)
        {
            _paused = paused;
            PublishGlobalClockLocked();
        }
    }

    /// <summary>
    ///     Connects a registered game key: stops the active simulation, creates a fresh one and
    ///     its pinned signal buffer. Unknown keys (frontend-only scenes) leave the engine idle.
    /// </summary>
    public void Connect(string game)
    {
        lock (_sync)
        {
            StopActiveLocked();

            if (_modules.TryGetValue(game, out var module))
            {
                var buffer = CreateBuffer(module.SignalName, module.Capacity);
                _activeModule = module;
                _activeSim = module.Create(_worldConfig, buffer);
                _activeBuffer = buffer;
            }

            _activeGame = game;
            _simulationTimeSeconds = 0d;
            _lastStepSeconds = 0d;
            _stepCount = 0;
            _accumulator = 0d;
            PublishGlobalClockLocked();
        }
    }

    private PinnedRenderBuffer<double> CreateBuffer(string eventName, int capacity)
    {
        var buffer = new PinnedRenderBuffer<double>(Math.Max(1, capacity));
        buffer.OnNotify = name => _notify(name, buffer.Ptr, buffer.ElementCount);
        return buffer;
    }

    private void StopActiveLocked()
    {
        if (_activeGame is null) return;

        if (_activeSim is not null)
        {
            _activeSim.Dispose();
            _activeSim = null;
        }

        // The per-connect pinned signal buffer owns a GCHandle: release it here so switching
        // demos never accumulates pinned arrays (the menu key reaches this path on purpose).
        if (_activeBuffer is not null)
        {
            _activeBuffer.Dispose();
            _activeBuffer = null;
            DisposedSignalBufferCount++;
        }

        _activeModule = null;
        _activeGame = null;
    }

    /// <summary>
    ///     Single-producer / single-consumer drain of the pinned input ring (mirrors the engine:
    ///     overruns clamp the tail forward — drop oldest, never block).
    /// </summary>
    private void DrainPinnedInputsLocked()
    {
        var head = Volatile.Read(ref _inputHeadBuffer.RawSpan[0]);
        if (head == _inputTail) return;

        var buffered = unchecked(head - _inputTail);
        if (buffered > InputRingLayout.QueueCapacity)
        {
            _droppedInputs += buffered - InputRingLayout.QueueCapacity;
            _inputTail = unchecked(head - InputRingLayout.QueueCapacity);
        }

        var data = _inputBuffer.RawSpan;
        while (_inputTail != head)
        {
            var start = (int)((uint)_inputTail % InputRingLayout.QueueCapacity) * InputRingLayout.SlotSize;
            if (InputDispatcher.Dispatch(data.Slice(start, InputRingLayout.SlotSize), _activeSim))
                _processedInputs++;
            else
                _droppedInputs++;
            _inputTail++;
        }
    }

    private void PublishGlobalClockLocked()
    {
        var span = _globalsBuffer.GetSpan(SignalBuffer.GlobalClockLength);
        var interpAlpha = FixedStepSeconds > 0 ? _accumulator / FixedStepSeconds : 0d;

        span[SignalBuffer.GlobalClockSeq] = _stepCount;
        span[SignalBuffer.GlobalClockTimeSeconds] = _simulationTimeSeconds;
        span[SignalBuffer.GlobalClockDeltaSeconds] = _lastStepSeconds;
        span[SignalBuffer.GlobalClockStepCount] = _stepCount;
        span[SignalBuffer.GlobalClockPaused] = _paused ? 1d : 0d;
        span[SignalBuffer.GlobalClockInterpAlpha] = Math.Clamp(interpAlpha, 0d, 1d);
        span[SignalBuffer.GlobalClockReserved0] = _processedInputs;
        span[SignalBuffer.GlobalClockReserved1] = _droppedInputs;

        _globalsBuffer.Commit(GlobalsEventName);
    }

    /// <summary>Test/diagnostic probe: writes one record into the pinned ring and publishes the head.</summary>
    internal void PushInputRecordForTest(ReadOnlySpan<double> record)
    {
        lock (_sync)
        {
            var data = _inputBuffer.RawSpan;
            var head = Volatile.Read(ref _inputHeadBuffer.RawSpan[0]);
            var start = (int)((uint)head % InputRingLayout.QueueCapacity) * InputRingLayout.SlotSize;
            record.Slice(0, InputRingLayout.SlotSize).CopyTo(data.Slice(start, InputRingLayout.SlotSize));
            Volatile.Write(ref _inputHeadBuffer.RawSpan[0], head + 1);
        }
    }

    /// <summary>Test/diagnostic probe: reads one globals slot from the pinned clock block.</summary>
    internal double ReadGlobalClockForTest(int index) => _globalsBuffer.RawSpan[index];

    public void Dispose()
    {
        lock (_sync)
        {
            StopActiveLocked();
            _globalsBuffer.Dispose();
            _inputBuffer.Dispose();
            _inputHeadBuffer.Dispose();
        }
    }
}
