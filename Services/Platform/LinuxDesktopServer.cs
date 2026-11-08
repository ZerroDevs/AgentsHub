#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentsHub.Services;

namespace AgentsHub.Services.Platform;

/// <summary>
/// Embedded local HTTP + WebSocket bridge server that hosts the React SPA and provides
/// bidirectional IPC for the Linux and macOS desktop applications without Microsoft WebView2.
/// </summary>
public sealed class LinuxDesktopServer : IDisposable
{
    private sealed class WebSocketClient(WebSocket socket)
    {
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim SendLock { get; } = new(1, 1);

        public async Task SendTextAsync(string text, CancellationToken ct = default)
        {
            if (Socket.State != WebSocketState.Open) return;
            await SendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (Socket.State == WebSocketState.Open)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(text);
                    await Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                SendLock.Release();
            }
        }
    }

    private readonly DashboardWebBridge _bridge;
    private readonly int _port;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private readonly ConcurrentDictionary<WebSocket, WebSocketClient> _clients = new();
    private readonly Lock _lock = new();
    private bool _isDisposed;

    private readonly string _webAssetsPath;

    public int Port => _port;
    public bool IsRunning => _listener?.IsListening ?? false;

    public LinuxDesktopServer(DashboardWebBridge bridge, int port = 9125)
    {
        _bridge = bridge;
        _port = port;

        string distPath = Path.Combine(AppContext.BaseDirectory, "ui", "dist");
        _webAssetsPath = Directory.Exists(distPath) ? distPath : Path.Combine(AppContext.BaseDirectory, "ui");
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_listener != null && _listener.IsListening) return;

            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            _listener.Prefixes.Add($"http://localhost:{_port}/");

            try
            {
                _listener.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LinuxDesktopServer] Failed to bind to port {_port}: {ex.Message}");
                return;
            }

            _bridge.WebMessageBroadcast += OnBridgeWebMessageBroadcast;
            _listenTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }
    }

    private void OnBridgeWebMessageBroadcast(string envelope)
    {
        if (string.IsNullOrEmpty(envelope) || _clients.IsEmpty) return;

        foreach (var (socket, client) in _clients)
        {
            if (socket.State == WebSocketState.Open)
            {
                _ = client.SendTextAsync(envelope);
            }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);
                _ = Task.Run(() => HandleRequestAsync(context, ct), ct);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    Console.WriteLine($"[LinuxDesktopServer] Request error: {ex.Message}");
                }
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            // Security: loopback requests only
            if (!IPAddress.IsLoopback(context.Request.RemoteEndPoint.Address))
            {
                context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                context.Response.Close();
                return;
            }

            string rawPath = context.Request.Url?.AbsolutePath ?? "/";

            // WebSocket Bridge endpoint - must be handled before modifying response headers
            if (rawPath.Equals("/ws", StringComparison.OrdinalIgnoreCase) && context.Request.IsWebSocketRequest)
            {
                await HandleWebSocketAsync(context, ct).ConfigureAwait(false);
                return;
            }

            // CORS headers for local desktop requests
            context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
            context.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            context.Response.Headers.Add("Access-Control-Allow-Headers", "*");

            if (context.Request.HttpMethod == "OPTIONS")
            {
                context.Response.StatusCode = 204;
                context.Response.Close();
                return;
            }

            // Static files serving
            await ServeStaticFileAsync(context, rawPath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                byte[] errorBytes = Encoding.UTF8.GetBytes(ex.Message);
                await context.Response.OutputStream.WriteAsync(errorBytes, ct).ConfigureAwait(false);
                context.Response.Close();
            }
            catch { }
        }
    }

    private async Task HandleWebSocketAsync(HttpListenerContext context, CancellationToken ct)
    {
        HttpListenerWebSocketContext wsContext;
        try
        {
            wsContext = await context.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LinuxDesktopServer] WebSocket accept failed: {ex.Message}");
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            context.Response.Close();
            return;
        }

        var socket = wsContext.WebSocket;
        var client = new WebSocketClient(socket);
        _clients.TryAdd(socket, client);

        byte[] buffer = new byte[8192];
        using var ms = new MemoryStream();

        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", ct).ConfigureAwait(false);
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    string message = Encoding.UTF8.GetString(ms.ToArray());
                    _ = _bridge.HandleIncomingJsonAsync(message);
                }
            }
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
        finally
        {
            _clients.TryRemove(socket, out _);
            try { socket.Dispose(); } catch { }
        }
    }

    private async Task ServeStaticFileAsync(HttpListenerContext context, string requestPath)
    {
        string relative = requestPath.TrimStart('/');
        if (string.IsNullOrEmpty(relative) || relative.Equals("index.html", StringComparison.OrdinalIgnoreCase))
        {
            relative = "index.html";
        }

        // Theme images endpoint
        if (relative.StartsWith("theme/", StringComparison.OrdinalIgnoreCase))
        {
            string themeFileName = Path.GetFileName(relative);
            string themeFile = Path.Combine(DashboardWebBridge.ThemeImageFolder, themeFileName);
            if (File.Exists(themeFile))
            {
                await SendFileAsync(context, themeFile).ConfigureAwait(false);
                return;
            }
        }

        string filePath = Path.GetFullPath(Path.Combine(_webAssetsPath, relative));

        // Path traversal guard
        if (!filePath.StartsWith(_webAssetsPath, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            context.Response.Close();
            return;
        }

        if (!File.Exists(filePath))
        {
            // SPA fallback: return index.html
            filePath = Path.Combine(_webAssetsPath, "index.html");
            if (!File.Exists(filePath))
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                context.Response.Close();
                return;
            }
        }

        await SendFileAsync(context, filePath).ConfigureAwait(false);
    }

    private static async Task SendFileAsync(HttpListenerContext context, string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        context.Response.ContentType = ext switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" or ".mjs" => "application/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            ".webp" => "image/webp",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            ".wasm" => "application/wasm",
            _ => "application/octet-stream"
        };

        context.Response.StatusCode = 200;
        using var fs = File.OpenRead(filePath);
        context.Response.ContentLength64 = fs.Length;
        await fs.CopyToAsync(context.Response.OutputStream).ConfigureAwait(false);
        context.Response.Close();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            _bridge.WebMessageBroadcast -= OnBridgeWebMessageBroadcast;

            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }

            foreach (var (socket, _) in _clients)
            {
                try { socket.Dispose(); } catch { }
            }
            _clients.Clear();

            _cts?.Dispose();
        }
    }
}
