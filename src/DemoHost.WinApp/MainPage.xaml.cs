using System.Diagnostics;
using DemoEngine.ECS;
using DemoEngine.Simulations;
using Game.BepuDemos;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace DemoHost;

/// <summary>
///     Test host page: a WebView2 showing the Babylon.js bundle while the native AOT process
///     runs the ported BepuPhysics2 demos and streams 3D transforms into the page through
///     WebView2 shared buffers.
///
///     Channel contract with the page (see <c>wwwroot/js/webview-bridge.js</c>):
///       host -> script  sharedbufferreceived (+ {channel, seq, elementCount} JSON)
///                       -> Float64Array view over shared memory, released after dispatch
///       host -> script  sharedbufferreceived (+ {channel:"input", slotSize, capacity} JSON,
///                       ReadWrite) -> the page writes input ring records into the mapping
///       script -> host  "connect:{game}" | "command:{game}:{verb}" | "pause:1" | "pause:0" | "input-hello"
/// </summary>
public sealed partial class MainPage : Page
{
    private const string AppName = "BepuDemosHost";

    private readonly SimulationHost _simHost;
    private readonly Dictionary<string, SharedBufferChannel> _channels = new();
    private readonly Dictionary<string, PendingFrame> _pending = new();
    private readonly object _pendingSync = new();

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherQueueTimer _simTimer;
    private TimeSpan _lastTick;

    private CoreWebView2? _core;
    private SharedInputChannel? _inputChannel;
    private LocalAssetServer? _assetServer;
    private int _flushQueued;

    private readonly record struct PendingFrame(nint Pointer, int ElementCount);

    public MainPage()
    {
        InitializeComponent();

        _simHost = new SimulationHost(OnBufferCommitted, new DemoRegistry().AddGameBepuDemosModules());

        // The simulations own no timer: the host pumps the fixed-step accumulator.
        _simTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _simTimer.Interval = TimeSpan.FromMilliseconds(16);
        _simTimer.IsRepeating = true;
        _simTimer.Tick += OnSimulationTick;
        _simTimer.Start();

        _ = InitializeBabylonEngineAsync();
    }

    /// <summary>UI-thread pump for the fixed-step simulation (<see cref="SimulationHost.Tick"/>).</summary>
    private void OnSimulationTick(DispatcherQueueTimer sender, object args)
    {
        // Input first: the page publishes records into the ReadWrite shared ring, the host
        // polls the head here — no per-input page -> host message exists.
        _inputChannel?.Drain();

        var now = _clock.Elapsed;
        var deltaSeconds = (now - _lastTick).TotalSeconds;
        _lastTick = now;
        _simHost.Tick(deltaSeconds);
    }

    private async Task InitializeBabylonEngineAsync()
    {
        try
        {
            // WebView2 picks up WEBVIEW2_USER_DATA_FOLDER when the browser process is created;
            // the Playwright harness passes a fresh per-run folder for isolated test runs.
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER")))
            {
                var userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName, "WebView2Data");
                Directory.CreateDirectory(userDataFolder);
                Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userDataFolder);
            }

            await BabylonWebView.EnsureCoreWebView2Async();
            _core = BabylonWebView.CoreWebView2;
            if (_core is null) return;

            _core.WebMessageReceived += OnWebMessageReceived;
            _inputChannel = new SharedInputChannel(_core, _simHost);

            // Loopback HTTP (with COOP/COEP) is the only way to make the desktop page
            // cross-origin isolated: the virtual-host mapping cannot send headers and
            // WebResourceRequested never fires for virtual-host URLs.
            var localAssetFolder = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            _assetServer = new LocalAssetServer(localAssetFolder);

            BabylonWebView.Source = new Uri(_assetServer.BaseUrl + "index.html");
        }
        catch (Exception ex)
        {
            // Error fallback (e.g., WebView2 runtime missing).
            Debug.WriteLine($"Failed to initialize WebView2: {ex}");
        }
    }

    /// <summary>Low-frequency page -> host commands (primitive strings, no JSON parsing).</summary>
    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        var message = args.TryGetWebMessageAsString();
        if (string.IsNullOrEmpty(message)) return;

        var separator = message.IndexOf(':');
        var verb = separator <= 0 ? message : message[..separator];
        var value = separator <= 0 ? string.Empty : message[(separator + 1)..];

        switch (verb)
        {
            case "connect":
                // Connect stops the previous simulation and releases its pinned signal buffer,
                // so the old shared mappings (and any queued frame) are dead weight: drop them
                // before the new sim starts committing.
                _simHost.Connect(value);
                DisposeChannels();
                break;
            case "pause":
                _simHost.SetPaused(value == "1");
                break;
            case "command":
                // value = "{game}:{verb}" — payload-free module command (spawn-ball/reset/…).
                var commandSeparator = value.IndexOf(':');
                if (commandSeparator > 0)
                    _simHost.SendCommand(value[..commandSeparator], value[(commandSeparator + 1)..]);
                break;
            case "input-hello":
                // The page registered its input views; post the writable ring mapping.
                _inputChannel?.PostToScript();
                break;
            default:
                Debug.WriteLine($"Unhandled page command: {message}");
                break;
        }
    }

    /// <summary>
    ///     Simulation callback (timer thread): coalesce to the newest frame per channel and
    ///     marshal onto the UI thread, where the WebView2 shared buffers live.
    /// </summary>
    private void OnBufferCommitted(string eventName, nint bufferPtr, int elementCount)
    {
        lock (_pendingSync)
        {
            _pending[eventName] = new PendingFrame(bufferPtr, elementCount);
        }

        if (Interlocked.Exchange(ref _flushQueued, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(FlushPending))
            Interlocked.Exchange(ref _flushQueued, 0);
    }

    private void FlushPending()
    {
        Interlocked.Exchange(ref _flushQueued, 0);

        KeyValuePair<string, PendingFrame>[] frames;
        lock (_pendingSync)
        {
            if (_pending.Count == 0) return;
            frames = new KeyValuePair<string, PendingFrame>[_pending.Count];
            var index = 0;
            foreach (var pair in _pending)
                frames[index++] = pair;
            _pending.Clear();
        }

        var core = _core;
        if (core is null) return;

        foreach (var (eventName, frame) in frames)
        {
            try
            {
                if (!_channels.TryGetValue(eventName, out var channel))
                {
                    channel = new SharedBufferChannel(core, eventName, Math.Max(4096, frame.ElementCount));
                    _channels[eventName] = channel;
                }

                // The pointer is valid for the lifetime of the pinned buffer; the copy into
                // shared memory happens synchronously here on the UI thread.
                channel.Post(frame.Pointer, frame.ElementCount);
            }
            catch (Exception ex)
            {
                // The UI-thread dispatcher fail-fasts on unhandled exceptions, so a broken
                // frame must never take the whole app down: log and keep the last good frame.
                Debug.WriteLine($"Shared-buffer post failed for '{eventName}': {ex}");
            }
        }
    }

    /// <summary>
    ///     Releases the per-signal WebView2 shared buffers and any queued frame. Called on every
    ///     connect (the previous sim's pinned buffer is gone) and on shutdown.
    /// </summary>
    private void DisposeChannels()
    {
        lock (_pendingSync)
        {
            _pending.Clear();
        }

        foreach (var channel in _channels.Values)
            channel.Dispose();
        _channels.Clear();
    }

    /// <summary>Stops the simulation and releases the WebView2 shared buffers.</summary>
    public void Shutdown()
    {
        _simTimer.Stop();
        _simHost.Dispose();
        _inputChannel?.Dispose();
        _inputChannel = null;
        _assetServer?.Dispose();
        _assetServer = null;
        DisposeChannels();
        _core = null;
    }
}
