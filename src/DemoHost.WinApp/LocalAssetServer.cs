using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DemoHost;

/// <summary>
///     Minimal in-process static file server for the WebView2 page shell.
///
///     WebView2's <c>SetVirtualHostNameToFolderMapping</c> cannot attach response headers and
///     the <c>WebResourceRequested</c> event does not fire for virtual-host URLs (documented
///     limitation), so the only way to make the desktop page cross-origin isolated is to serve
///     it over loopback HTTP where every response carries COOP/COEP. The desktop host runs the
///     native simulation and never the .NET WASM runtime, but the page is then isolated exactly
///     like the browser host.
///
///     Raw <see cref="TcpListener"/> on 127.0.0.1 with an OS-assigned port: no HTTP.sys URL
///     ACLs (unlike <c>HttpListener</c>), no admin requirement, no port collisions.
/// </summary>
public sealed class LocalAssetServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly string _root;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Rooted base URL, e.g. <c>http://127.0.0.1:54123/</c>.</summary>
    public string BaseUrl { get; }

    public LocalAssetServer(string root)
    {
        _root = Path.GetFullPath(root);
        if (!Directory.Exists(_root))
            throw new DirectoryNotFoundException($"asset root not found: {_root}");

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUrl = $"http://127.0.0.1:{port}/";
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => Serve(client), _cts.Token);
        }
    }

    private void Serve(TcpClient client)
    {
        try
        {
            using (client)
            {
                using var stream = client.GetStream();

                var requestLine = ReadLine(stream);
                if (string.IsNullOrEmpty(requestLine)) return;

                var parts = requestLine.Split(' ', 3);
                if (parts.Length < 2)
                {
                    WriteResponse(stream, "400 Bad Request", "text/plain; charset=utf-8", "bad request", isolationHeaders: false);
                    return;
                }

                // Drain the request headers (GET only; there is no request body).
                while (!string.IsNullOrEmpty(ReadLine(stream))) { }

                if (!string.Equals(parts[0], "GET", StringComparison.Ordinal))
                {
                    WriteResponse(stream, "405 Method Not Allowed", "text/plain; charset=utf-8", "method not allowed", isolationHeaders: false);
                    return;
                }

                var path = parts[1];
                var query = path.IndexOf('?');
                if (query >= 0) path = path[..query];

                string relative;
                try
                {
                    relative = Uri.UnescapeDataString(path).TrimStart('/');
                }
                catch (UriFormatException)
                {
                    WriteResponse(stream, "400 Bad Request", "text/plain; charset=utf-8", "bad request", isolationHeaders: false);
                    return;
                }

                if (relative.Length == 0) relative = "index.html";

                var full = Path.GetFullPath(Path.Combine(_root, relative));
                if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                {
                    WriteResponse(stream, "404 Not Found", "text/plain; charset=utf-8", "not found", isolationHeaders: false);
                    return;
                }

                WriteResponse(stream, "200 OK", ContentTypeFor(Path.GetExtension(full)),
                    File.ReadAllBytes(full), isolationHeaders: true);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"asset server failed: {ex}");
        }
    }

    /// <summary>Reads one CRLF-terminated ASCII line; returns null at EOF/oversized junk.</summary>
    private static string? ReadLine(Stream stream)
    {
        var buffer = new byte[1];
        var line = new StringBuilder(128);
        while (true)
        {
            var read = stream.Read(buffer, 0, 1);
            if (read == 0) return line.Length > 0 ? line.ToString() : null;

            var character = (char)buffer[0];
            if (character == '\n') return line.ToString().TrimEnd('\r');

            line.Append(character);
            if (line.Length > 8192) return null;
        }
    }

    private static void WriteResponse(Stream stream, string status, string contentType, string body, bool isolationHeaders)
        => WriteResponse(stream, status, contentType, Encoding.UTF8.GetBytes(body), isolationHeaders);

    private static void WriteResponse(Stream stream, string status, string contentType, byte[] body, bool isolationHeaders)
    {
        var head = new StringBuilder(256);
        head.Append("HTTP/1.1 ").Append(status).Append("\r\n");
        head.Append("Content-Type: ").Append(contentType).Append("\r\n");
        head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        head.Append("Cache-Control: no-store\r\n");
        if (isolationHeaders)
        {
            // The reason this server exists: a SharedArrayBuffer-capable (cross-origin
            // isolated) page, matching what the browser host gets from COOP/COEP headers.
            head.Append("Cross-Origin-Opener-Policy: same-origin\r\n");
            head.Append("Cross-Origin-Embedder-Policy: require-corp\r\n");
        }

        head.Append("Connection: close\r\n\r\n");

        var headBytes = Encoding.ASCII.GetBytes(head.ToString());
        stream.Write(headBytes, 0, headBytes.Length);
        if (body.Length > 0) stream.Write(body, 0, body.Length);
        stream.Flush();
    }

    private static string ContentTypeFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript",
        ".css" => "text/css",
        ".json" or ".map" => "application/json",
        ".wasm" => "application/wasm",
        ".bin" => "application/octet-stream",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".ico" => "image/x-icon",
        ".webmanifest" => "application/manifest+json",
        ".mp3" => "audio/mpeg",
        ".ogg" => "audio/ogg",
        ".wav" => "audio/wav",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch (Exception)
        {
            // Already stopped.
        }

        _cts.Dispose();
    }
}
