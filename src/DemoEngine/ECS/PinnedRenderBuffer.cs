using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DemoEngine.ECS;

/// <summary>
///     Zero-copy render buffer: a pinned managed array (<typeparamref name="T"/> = <c>double</c>
///     for every production signal) whose address is handed to the presentation layer. The
///     WinApp host memcpy's the same span into a WebView2 shared buffer, where the page reads
///     it as a <c>Float64Array</c>.
///     Mirrors <c>Game.Engine.ECS.PinnedRenderBuffer&lt;T&gt;</c>.
///
///     The capacity is fixed for the lifetime of the buffer: the address is handed to script
///     once, so a re-pin (pointer change) is never permitted. Exceeding <see cref="Capacity"/>
///     fails fast instead of silently invalidating every cached typed-array view.
/// </summary>
/// <typeparam name="T">Blittable scalar element type of the signal buffer.</typeparam>
public sealed class PinnedRenderBuffer<T> : IDisposable
    where T : unmanaged
{
    private readonly T[] _buffer;
    private GCHandle _handle;
    private Action<string>? _notify;

    public Action<string>? OnNotify { set => _notify = value; }

    public PinnedRenderBuffer(int initialCapacity)
    {
        _buffer = new T[initialCapacity];
        _handle = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
    }

    /// <summary>Address of the pinned array; valid for the lifetime of the buffer (never re-pinned).</summary>
    public IntPtr Ptr => _handle.AddrOfPinnedObject();

    /// <summary>Total element capacity; fixed at construction (never re-pinned).</summary>
    public int Capacity => _buffer.Length;

    /// <summary>Number of elements written by the last <see cref="GetSpan"/> call.</summary>
    public int ElementCount { get; private set; }

    /// <summary>Size in bytes of one element (8 for double).</summary>
    public int ElementSize => Unsafe.SizeOf<T>();

    /// <summary>Full-capacity writable view over the pinned storage (input ring cursor path).</summary>
    public Span<T> RawSpan => _buffer.AsSpan();

    public Span<T> GetSpan(int elementCount)
    {
        if (elementCount > _buffer.Length)
        {
            throw new InvalidOperationException(
                $"Signal buffer overflow: {elementCount} elements requested, capacity is {_buffer.Length}. " +
                "The pinned address is shared with the presentation layer and must never move.");
        }

        ElementCount = elementCount;
        return _buffer.AsSpan(0, elementCount);
    }

    public void Commit(string eventName)
    {
        _notify?.Invoke(eventName);
    }

    public void Dispose()
    {
        if (_handle.IsAllocated)
            _handle.Free();
    }
}
