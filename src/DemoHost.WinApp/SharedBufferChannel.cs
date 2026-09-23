using Microsoft.Web.WebView2.Core;

namespace DemoHost;

/// <summary>
///     Host half of the zero-copy render channel between the native AOT engine and the
///     Babylon.js page in WebView2. Backed by <c>CoreWebView2Environment.CreateSharedBuffer</c>:
///     the host writes directly into the shared mapping, then calls
///     <c>PostSharedBufferToScript</c> (ReadOnly) so the page receives the very same memory
///     as an <c>ArrayBuffer</c> — no JSON payload, no per-frame serialization and no
///     per-entity interop calls.
///
///     The scalar element type is always 8-byte double (Float64Array) — the ABI has no
///     scalar-size field. Metadata carries <c>channel</c>, <c>seq</c> and <c>elementCount</c>
///     only.
///
///     Frames rotate over three buffers: the page may still be reading frame N while the
///     simulation writes frame N+1, and each script-side view is released immediately after
///     the synchronous dispatch (see <c>webview-bridge.js</c> — values are only valid inside
///     the listener callback).
/// </summary>
internal sealed class SharedBufferChannel : IDisposable
{
    private const int BufferCount = 3;

    /// <summary>Bytes per scalar element — every signal is pure 64-bit.</summary>
    private const int ScalarSize = sizeof(double);

    private readonly CoreWebView2 _core;
    private readonly string _channel;

    private readonly CoreWebView2SharedBuffer[] _buffers = new CoreWebView2SharedBuffer[BufferCount];
    private readonly Stream[] _streams = new Stream[BufferCount];

    private byte[] _staging = Array.Empty<byte>();
    private int _capacityElements;
    private int _next;
    private long _seq;

    public SharedBufferChannel(CoreWebView2 core, string channel, int capacityElements)
    {
        _core = core;
        _channel = channel;
        Allocate(capacityElements);
    }

    private void Allocate(int capacityElements)
    {
        _capacityElements = capacityElements;
        var byteSize = (ulong)((long)capacityElements * ScalarSize);
        for (var i = 0; i < BufferCount; i++)
        {
            _buffers[i] = _core.Environment.CreateSharedBuffer(byteSize);
            // OpenStream() returns a WinRT IRandomAccessStream; AsStream() adapts it to
            // System.IO.Stream (seekable, backed by the shared file mapping).
            _streams[i] = _buffers[i].OpenStream().AsStream();
        }
    }

    /// <summary>
    ///     Copies one committed signal (<paramref name="elementCount"/> scalar elements at
    ///     <paramref name="pointer"/>) into the next shared buffer and posts it to script.
    ///     Must be called on the UI thread that owns the WebView2.
    /// </summary>
    public void Post(nint pointer, int elementCount)
    {
        var byteCount = elementCount * ScalarSize;

        if (byteCount > _capacityElements * ScalarSize)
        {
            Free();
            Allocate(elementCount);
        }

        if (_staging.Length < byteCount)
            _staging = new byte[byteCount];

        unsafe
        {
            new ReadOnlySpan<byte>((void*)pointer, byteCount).CopyTo(_staging);
        }

        var index = _next;
        _next = (_next + 1) % BufferCount;

        var stream = _streams[index];
        stream.Seek(0, SeekOrigin.Begin);
        stream.Write(_staging, 0, byteCount);
        stream.Flush();

        _seq++;
        var metadata =
            $"{{\"channel\":\"{_channel}\",\"seq\":{_seq},\"elementCount\":{elementCount}}}";
        _core.PostSharedBufferToScript(_buffers[index], CoreWebView2SharedBufferAccess.ReadOnly, metadata);
    }

    private void Free()
    {
        for (var i = 0; i < BufferCount; i++)
        {
            _streams[i]?.Dispose();
            _buffers[i]?.Dispose();
            _streams[i] = null!;
            _buffers[i] = null!;
        }
    }

    public void Dispose()
    {
        Free();
    }
}
