using DemoEngine.Config;
using DemoEngine.ECS;

namespace DemoEngine.Simulations;

/// <summary>
///     One test-fixture simulation owned by <c>SimulationHost</c> and advanced by the
///     host-pumped fixed-step loop. Mirrors the engine's
///     <c>Game.Engine.Simulations.IGameSimulation</c>.
/// </summary>
public interface IDemoSimulation : IDisposable
{
    /// <summary>Advances the simulation by one fixed step (host-pumped; simulations own no timers).</summary>
    void Step(double deltaSeconds);
}

/// <summary>Payload-free verbs exposed through the generic host command path.</summary>
public interface IDemoCommands
{
    bool TryCommand(string verb);
}

/// <summary>
///     Descriptor for one registrable demo simulation: the scene key, the pinned signal buffer
///     geometry and the factory that creates the simulation. Mirrors
///     <c>Game.Engine.Simulations.GameModule&lt;TSignal&gt;</c>.
///
///     Modules are registered by hand-written glue
///     (<c>Game.BepuDemos.BepuDemosModules.AddGameBepuDemosModules</c>) — direct static calls,
///     no reflection, so the AOT trimmer keeps every simulation reachable from the host entry
///     point. The engine uses a source generator for this; see docs/compat-review.md.
/// </summary>
/// <typeparam name="TSignal">Batched render-signal record emitted by the simulation.</typeparam>
public sealed class DemoModule<TSignal>
{
    public DemoModule(
        string gameKey,
        string signalName,
        int capacity,
        Func<DemoWorldConfig?, IRenderTransport<TSignal>, IDemoSimulation> create,
        Func<TSignal, int> elementLength,
        Action<TSignal, Span<double>> encode)
    {
        GameKey = gameKey;
        SignalName = signalName;
        Capacity = capacity;
        Create = create;
        ElementLength = elementLength;
        Encode = encode;
    }

    /// <summary>Scene key the presentation layer connects with (<c>[a-z0-9-]+</c>).</summary>
    public string GameKey { get; }

    /// <summary>Signal buffer name exposed to the hosts (e.g. `transform3d`).</summary>
    public string SignalName { get; }

    /// <summary>Fixed element capacity of the pinned signal buffer (never grows).</summary>
    public int Capacity { get; }

    /// <summary>Creates one fresh simulation instance for a <c>Connect(gameKey)</c> call.</summary>
    public Func<DemoWorldConfig?, IRenderTransport<TSignal>, IDemoSimulation> Create { get; }

    /// <summary>Element length of one batched signal (header + records).</summary>
    public Func<TSignal, int> ElementLength { get; }

    /// <summary>Encodes one batched signal into the pinned float64 buffer.</summary>
    public Action<TSignal, Span<double>> Encode { get; }
}

/// <summary>
///     Non-generic, AOT-shaped module entry: the registry closes over the typed transport
///     creation, so <c>SimulationHost</c> can create simulations from a
///     <c>Dictionary&lt;string, DemoModuleEntry&gt;</c> without any reflection.
/// </summary>
public sealed class DemoModuleEntry
{
    private readonly Func<DemoWorldConfig?, PinnedRenderBuffer<double>, IDemoSimulation> _create;

    public DemoModuleEntry(
        string gameKey,
        string signalName,
        int capacity,
        Func<DemoWorldConfig?, PinnedRenderBuffer<double>, IDemoSimulation> create)
    {
        GameKey = gameKey;
        SignalName = signalName;
        Capacity = capacity;
        _create = create;
    }

    public string GameKey { get; }

    public string SignalName { get; }

    public int Capacity { get; }

    public IDemoSimulation Create(DemoWorldConfig? config, PinnedRenderBuffer<double> signalBuffer) =>
        _create(config, signalBuffer);
}

/// <summary>
///     Assembles the demo modules a host runs. Mirrors <c>Game.Engine.Simulations.EngineBuilder</c>;
///     the per-assembly <c>AddGameBepuDemosModules()</c> glue is hand-written (AOT-safe, static).
/// </summary>
public sealed class DemoRegistry
{
    private readonly List<DemoModuleEntry> _modules = new();

    public IReadOnlyList<DemoModuleEntry> Modules => _modules;

    /// <summary>Game keys registered so far, in registration order.</summary>
    public IReadOnlyList<string> GameKeys => _modules.ConvertAll(static module => module.GameKey);

    /// <summary>
    ///     Registers one module. Throws when the game key is already taken — duplicate keys
    ///     would make <c>Connect(gameKey)</c> ambiguous.
    /// </summary>
    public DemoRegistry AddModule<TSignal>(DemoModule<TSignal> module)
    {
        for (var i = 0; i < _modules.Count; i++)
        {
            if (_modules[i].GameKey == module.GameKey)
            {
                throw new InvalidOperationException(
                    $"Game key '{module.GameKey}' is already registered (signal '{_modules[i].SignalName}').");
            }
        }

        _modules.Add(new DemoModuleEntry(module.GameKey, module.SignalName, module.Capacity,
            (config, buffer) => module.Create(config,
                new DirectRenderTransport<TSignal, double>(
                    module.SignalName, module.ElementLength, module.Encode, buffer))));
        return this;
    }
}
