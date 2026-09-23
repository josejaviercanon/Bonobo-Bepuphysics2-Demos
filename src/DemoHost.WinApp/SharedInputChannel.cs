using System.Runtime.InteropServices;
using DemoEngine.ECS;
using DemoEngine.Inputs;
using Microsoft.Web.WebView2.Core;

namespace DemoHost;

/// <summary>
///     Host half of the zero-copy input ring on the desktop (WebView2) host. The host creates
///     one <see cref="CoreWebView2SharedBuffer" /> and posts it to script with
///     <see cref="CoreWebView2SharedBufferAccess.ReadWrite" />, so the page writes records
///     straight into the shared mapping (payload + head counter) exactly like the browser
///     host writes the WASM heap. The host never receives a message per input: it polls the
///     head from its existing simulation timer and reads only the new records through
///     <see cref="CoreWebView2SharedBuffer.OpenStream" />, then hands each 8-double slot to
///     the generated dispatcher.
///
///     A fresh stream is opened per drain on purpose: a stream opened before the buffer was
///     posted to script serves stale reads (the page's writes are invisible on that handle,
///     while the same handle's own writes do reach the page), verified in the desktop smoke.
///
///     Layout is byte-identical to the browser ring: <c>capacity × slotSize</c> doubles
///     followed by one 32-bit head counter (<c>InputRingLayout</c>), mirrored in TypeScript
///     by <c>Frontend/generated/InputLayouts.ts</c>. Overruns clamp the tail forward
///     (drop oldest) — the producer never blocks.
/// </summary>
internal sealed class SharedInputChannel : IDisposable
{
    private const int HeadBytes = sizeof(int);

    private readonly CoreWebView2 _core;
    private readonly SimulationHost _simHost;
    private readonly CoreWebView2SharedBuffer _buffer;
    private readonly int _capacityRecords;
    private readonly int _headOffset;
    private readonly byte[] _headBytes = new byte[HeadBytes];
    private readonly byte[] _recordBytes;
    private readonly string _metadata;

    private int _tail;
    private long _dropped;

    public SharedInputChannel(CoreWebView2 core, SimulationHost simHost,
        int capacityRecords = InputRingLayout.QueueCapacity, int slotSize = InputRingLayout.SlotSize)
    {
        _core = core;
        _simHost = simHost;
        _capacityRecords = capacityRecords;
        _headOffset = capacityRecords * slotSize * sizeof(double);

        _buffer = core.Environment.CreateSharedBuffer((ulong)(_headOffset + HeadBytes));
        _recordBytes = new byte[slotSize * sizeof(double)];
        _metadata = $"{{\"channel\":\"input\",\"slotSize\":{slotSize},\"capacity\":{capacityRecords}}}";
    }

    /// <summary>Records dropped because the page overran the fixed ring capacity.</summary>
    public long DroppedInputCount => _dropped;

    /// <summary>
    ///     Posts (or re-posts, after a page reload) the writable input mapping. The script
    ///     keeps the mapping for the page lifetime on purpose — it is this channel's producer
    ///     surface, not a per-frame payload — so it must not call
    ///     <c>chrome.webview.releaseBuffer</c> on it.
    /// </summary>
    public void PostToScript() =>
        _core.PostSharedBufferToScript(_buffer, CoreWebView2SharedBufferAccess.ReadWrite, _metadata);

    /// <summary>
    ///     Reads every record published since the last drain and routes it through
    ///     <see cref="SimulationHost.ProcessInputRecord" />. Must run on the WebView2 UI
    ///     thread (it touches the shared-buffer stream).
    /// </summary>
    public int Drain()
    {
        byte[]? pending = null;

        using (var stream = _buffer.OpenStream().AsStream())
        {
            stream.Seek(_headOffset, SeekOrigin.Begin);
            stream.ReadExactly(_headBytes);
            var head = BitConverter.ToInt32(_headBytes, 0);
            if (head == _tail) return 0;

            if (unchecked(head - _tail) < 0)
            {
                // The page reloaded and its producer counter restarted at 0; drop any stale
                // records still sitting in the shared mapping.
                _tail = head;
                return 0;
            }

            var buffered = unchecked(head - _tail);
            if (buffered > _capacityRecords)
            {
                _dropped += buffered - _capacityRecords;
                _tail = unchecked(head - _capacityRecords);
            }

            // Copy the pending records out while the stream is open, then decode — the
            // dispatcher must not run with the stream object in scope.
            var pendingCount = unchecked(head - _tail);
            pending = new byte[pendingCount * _recordBytes.Length];

            for (var i = 0; i < pendingCount; i++)
            {
                var slot = (int)((uint)(_tail + i) % _capacityRecords);
                stream.Seek(slot * _recordBytes.Length, SeekOrigin.Begin);
                stream.ReadExactly(_recordBytes);
                _recordBytes.CopyTo(pending, i * _recordBytes.Length);
            }

            _tail = head;
        }

        var drained = 0;
        for (var i = 0; i < pending.Length; i += _recordBytes.Length)
        {
            _simHost.ProcessInputRecord(
                MemoryMarshal.Cast<byte, double>(pending.AsSpan(i, _recordBytes.Length)));
            drained++;
        }

        return drained;
    }

    public void Dispose() => _buffer.Dispose();
}
