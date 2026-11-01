#nullable enable
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using AgentsHub.Helpers;

namespace AgentsHub.Services;

public sealed partial class CliWebTerminalServer : IDisposable
{
    private sealed class WebSocketClient(WebSocket socket)
    {
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim SendLock { get; } = new(1, 1);

        public async Task SendTextAsync(string text, CancellationToken ct)
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

    /// <summary>
    /// One HTTP + WebSocket endpoint serving a single terminal session. Keyed by session id
    /// rather than CliType, so every Antigravity profile can have its own live port.
    /// </summary>
    private sealed class EndpointListener(string sessionId, string displayName, int port, string token)
    {
        public string SessionId { get; } = sessionId;
        public string DisplayName { get; } = displayName;
        public int Port { get; } = port;
        /// <summary>Handed to the served page only, so a page without it cannot drive the terminal.</summary>
        public string Token { get; } = token;
        public HttpListener? Listener { get; set; }
        public ConcurrentDictionary<WebSocket, WebSocketClient> Sockets { get; } = new();
    }

    /// <summary>
    /// The terminal page needs xterm and its own stylesheet, nothing else from the app folder. A
    /// fixed list also removes the path join that turned a request path into a filesystem path.
    /// </summary>
    private static readonly HashSet<string> AllowedAssets = new(StringComparer.OrdinalIgnoreCase)
    {
        "terminal.html",
        "js/xterm.js",
        "js/xterm-addon-fit.js",
        "css/xterm.css",
        "cli.png",
        "app.png"
    };

    private readonly CliManagerService _cliManager;
    private readonly string _webAssetsDir;
    private readonly ConcurrentDictionary<string, EndpointListener> _endpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private bool _isDisposed;

    public CliWebTerminalServer(CliManagerService cliManager)
    {
        _cliManager = cliManager;
        // Served straight out of ui/, the same tree the embedded WebViews load. There used to be a
        // second flat copy under assets/web that had to be hand-synced with this one, and the
        // terminal page served over the LAN was the copy nobody remembered to update.
        _webAssetsDir = Path.Combine(AppContext.BaseDirectory, "ui");

        // Fixed endpoints. Antigravity profiles register their own on launch through
        // EnsureEndpoint, because each profile binds its own allocated port.
        _endpoints["claude_code"] = new EndpointListener(
            "claude_code", "Claude Code", 9229, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
        _endpoints["antigravity_cli"] = new EndpointListener(
            "antigravity_cli", "Antigravity CLI", 9339, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));

        // Subscribe to the id-based events: the CliType events collapse every Antigravity
        // profile onto CliType.AntigravityCli, which would feed one profile's output to another
        // profile's endpoint.
        _cliManager.TerminalDataReceivedId += OnTerminalDataReceived;
        _cliManager.StatusChangedId += OnStatusChanged;
        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    public void Start()
    {
        foreach (var endpoint in _endpoints.Values)
        {
            StartEndpoint(endpoint);
        }
    }

    /// <summary>
    /// Binds an endpoint for a terminal session - used for Antigravity profiles, which listen on
    /// their own allocated port. Idempotent, and replaces the listener if the port changed.
    /// </summary>
    public void EnsureEndpoint(string sessionId, string displayName, int port)
    {
        if (_isDisposed || string.IsNullOrWhiteSpace(sessionId) || port <= 0) return;

        if (_endpoints.TryGetValue(sessionId, out var existing))
        {
            if (existing.Port == port) return;

            // The profile was reallocated to a different port; retire the stale listener.
            _endpoints.TryRemove(sessionId, out _);
            StopEndpoint(existing);
        }

        var endpoint = new EndpointListener(
            sessionId,
            displayName,
            port,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
        if (_endpoints.TryAdd(sessionId, endpoint))
        {
            StartEndpoint(endpoint);
        }
    }

    private static void StopEndpoint(EndpointListener endpoint)
    {
        try
        {
            endpoint.Listener?.Stop();
            endpoint.Listener?.Close();
        }
        catch { }

        foreach (var client in endpoint.Sockets.Values)
        {
            try { client.Socket.Dispose(); } catch { }
        }
        endpoint.Sockets.Clear();
    }

    private void StartEndpoint(EndpointListener endpoint)
    {
        try
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{endpoint.Port}/");
            listener.Prefixes.Add($"http://localhost:{endpoint.Port}/");
            listener.Start();
            endpoint.Listener = listener;

            _ = Task.Run(() => AcceptRequestsLoopAsync(endpoint, listener, _cts.Token));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to bind CLI web terminal on port {endpoint.Port}: {ex.Message}");
        }
    }

    private async Task AcceptRequestsLoopAsync(EndpointListener endpoint, HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            try
            {
                var context = await listener.GetContextAsync().ConfigureAwait(false);
                _ = Task.Run(() => HandleRequestAsync(endpoint, context, ct), ct);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested || !listener.IsListening)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception)
            {
                // Suppress transient accept errors
            }
        }
    }

    private async Task HandleRequestAsync(EndpointListener endpoint, HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            // This listener only ever serves the machine it runs on. A raw LAN forwarder cannot
            // authenticate a terminal, so a remote socket is refused instead of exposed.
            var remote = context.Request.RemoteEndPoint?.Address;
            if (remote != null && !IPAddress.IsLoopback(remote))
            {
                context.Response.StatusCode = 403;
                context.Response.Close();
                return;
            }

            string path = context.Request.Url?.AbsolutePath ?? "/";

            if (context.Request.IsWebSocketRequest)
            {
                if (path == "/ws" && IsAllowedOrigin(context) && HasValidToken(context, endpoint))
                {
                    await HandleWebSocketAsync(endpoint, context, ct).ConfigureAwait(false);
                    return;
                }

                context.Response.StatusCode = 403;
                context.Response.Close();
                return;
            }

            string relative = path is "/" or "/index.html"
                ? "terminal.html"
                : path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);

            // The allowlist is written with forward slashes; the filesystem path is not.
            string assetKey = relative.Replace(Path.DirectorySeparatorChar, '/');
            if (AllowedAssets.Contains(assetKey))
            {
                await ServeFileAsync(context, relative, ContentTypeFor(relative), endpoint.Token).ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = 404;
            context.Response.Close();
        }
        catch
        {
            try { context.Response.Close(); } catch { }
        }
    }

    private static bool HasValidToken(HttpListenerContext context, EndpointListener endpoint)
    {
        string? token = context.Request.QueryString["token"];
        if (string.IsNullOrEmpty(token)) return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(token),
            Encoding.UTF8.GetBytes(endpoint.Token));
    }

    /// <summary>
    /// A page on another site can open a WebSocket to loopback; the browser sends its own Origin
    /// and cannot forge it, so anything that is not this machine's own page is refused.
    /// </summary>
    private static bool IsAllowedOrigin(HttpListenerContext context)
    {
        string? origin = context.Request.Headers["Origin"];
        if (string.IsNullOrEmpty(origin)) return true;

        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback;
    }

    private static string ContentTypeFor(string relativeFile) => Path.GetExtension(relativeFile).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "application/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".png" => "image/png",
        _ => "application/octet-stream"
    };

    private async Task ServeFileAsync(HttpListenerContext context, string relativeFile, string contentType, string token)
    {
        string fullPath = Path.GetFullPath(Path.Combine(_webAssetsDir, relativeFile));
        string assetsRoot = Path.GetFullPath(_webAssetsDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        byte[] bytes;
        if (relativeFile.Equals("terminal.html", StringComparison.OrdinalIgnoreCase) ||
            relativeFile.EndsWith("terminal.html", StringComparison.OrdinalIgnoreCase))
        {
            string html = await File.ReadAllTextAsync(fullPath).ConfigureAwait(false);

            // Both values land inside the page's CSS and script, so they are checked as a value,
            // never interpolated as text.
            string reqTheme = context.Request.QueryString["theme"] ?? (ThemeManager.CurrentTheme == AppTheme.Light ? "light" : "dark");
            reqTheme = string.Equals(reqTheme, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";

            string reqAccent = context.Request.QueryString["accent"] ?? ThemeManager.AccentHex;
            if (!AccentRegex().IsMatch(reqAccent)) reqAccent = ThemeManager.AccentHex;
            if (!AccentRegex().IsMatch(reqAccent)) reqAccent = "#4049B9";

            bool isLight = reqTheme.Equals("light", StringComparison.OrdinalIgnoreCase);
            var (h, _, _) = ThemeManager.HexToHsl(reqAccent);
            double lum = ThemeManager.GetLuminance(reqAccent);
            string accentText = lum > 0.62 ? "#0a0f14" : "#ffffff";

            string bgTerminal = isLight ? $"hsl({h}, 22%, 95.5%)" : $"hsl({h}, 22%, 5.5%)";
            string bgOverlay = isLight ? $"hsla({h}, 22%, 95.5%, 0.94)" : $"hsla({h}, 22%, 5.5%, 0.94)";
            string bgCard = isLight ? "#ffffff" : $"hsl({h}, 26%, 12.5%)";
            string borderSubtle = isLight ? $"hsl({h}, 25%, 83%)" : $"hsl({h}, 24%, 19%)";
            string textMain = isLight ? $"hsl({h}, 28%, 11%)" : "#f8fafc";
            string textMuted = isLight ? $"hsl({h}, 16%, 36%)" : $"hsl({h}, 12%, 68%)";
            string textDim = isLight ? $"hsl({h}, 10%, 52%)" : $"hsl({h}, 10%, 48%)";

            string dynamicRoot = $":root {{\n      --bg-terminal: {bgTerminal};\n      --bg-overlay: {bgOverlay};\n      --bg-card: {bgCard};\n      --border-subtle: {borderSubtle};\n      --text-main: {textMain};\n      --text-muted: {textMuted};\n      --text-dim: {textDim};\n      --accent-green: {reqAccent};\n      --accent-text: {accentText};\n    }}";

            var rootRegex = MyRegex();
            if (rootRegex.IsMatch(html))
            {
                html = rootRegex.Replace(html, dynamicRoot, 1);
            }

            string scriptInject = $"<script>window.__INITIAL_THEME__='{reqTheme}';window.__INITIAL_ACCENT__='{reqAccent}';window.__TERMINAL_TOKEN__='{token}';</script>\n</head>";
            html = html.Replace("</head>", scriptInject, StringComparison.OrdinalIgnoreCase);

            bytes = Encoding.UTF8.GetBytes(html);
        }
        else
        {
            bytes = await File.ReadAllBytesAsync(fullPath).ConfigureAwait(false);
        }

        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        context.Response.Headers.Add("X-Content-Type-Options", "nosniff");
        context.Response.Headers.Add("Referrer-Policy", "no-referrer");
        context.Response.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
        context.Response.StatusCode = 200;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    private async Task HandleWebSocketAsync(EndpointListener endpoint, HttpListenerContext context, CancellationToken ct)
    {
        HttpListenerWebSocketContext wsContext;
        try
        {
            wsContext = await context.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
        }
        catch
        {
            context.Response.StatusCode = 500;
            context.Response.Close();
            return;
        }

        var webSocket = wsContext.WebSocket;
        var client = new WebSocketClient(webSocket);
        endpoint.Sockets.TryAdd(webSocket, client);

        try
        {
            var info = _cliManager.GetInfo(endpoint.SessionId);
            var history = _cliManager.GetTerminalHistory(endpoint.SessionId);
            var initPayload = new Models.TerminalInitPayload(
                "init",
                endpoint.DisplayName,
                info.State == CliState.Running,
                info.StatusText,
                history);

            string initJson = JsonSerializer.Serialize(initPayload);
            await client.SendTextAsync(initJson, ct).ConfigureAwait(false);

            // Send current active theme immediately
            string currentTheme = ThemeManager.CurrentTheme == AppTheme.Light ? "light" : "dark";
            string currentAccent = ThemeManager.AccentHex;
            var themePayload = new { type = "theme_changed", theme = currentTheme, accentColor = currentAccent };
            string themeJson = JsonSerializer.Serialize(themePayload);
            await client.SendTextAsync(themeJson, ct).ConfigureAwait(false);

            byte[] buffer = new byte[8192];
            using var ms = new MemoryStream();

            while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", ct).ConfigureAwait(false);
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    string incomingJson = Encoding.UTF8.GetString(ms.ToArray());
                    using var doc = JsonDocument.Parse(incomingJson);
                    if (doc.RootElement.TryGetProperty("type", out var typeEl))
                    {
                        string? msgType = typeEl.GetString();
                        switch (msgType)
                        {
                            case "input":
                                if (doc.RootElement.TryGetProperty("data", out var dataEl))
                                {
                                    string? text = dataEl.GetString();
                                    if (text != null)
                                    {
                                        _cliManager.SendInput(endpoint.SessionId, text);
                                    }
                                }
                                break;

                            case "resize":
                                if (doc.RootElement.TryGetProperty("cols", out var colsEl) &&
                                    doc.RootElement.TryGetProperty("rows", out var rowsEl))
                                {
                                    short cols = colsEl.GetInt16();
                                    short rows = rowsEl.GetInt16();
                                    _cliManager.Resize(endpoint.SessionId, cols, rows);
                                }
                                break;

                            case "start":
                                _cliManager.Start(endpoint.SessionId);
                                break;
                        }
                    }
                }
            }
        }
        catch
        {
            // Socket disconnected
        }
        finally
        {
            endpoint.Sockets.TryRemove(webSocket, out _);
            try
            {
                if (webSocket.State == WebSocketState.Open)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposed", CancellationToken.None).ConfigureAwait(false);
                }
                webSocket.Dispose();
            }
            catch { }
        }
    }

    private void OnTerminalDataReceived(string sessionId, string text)
    {
        if (!_endpoints.TryGetValue(sessionId, out var endpoint) || endpoint.Sockets.IsEmpty) return;

        var payload = new Models.TerminalOutputPayload("output", text);
        string json = JsonSerializer.Serialize(payload);

        foreach (var client in endpoint.Sockets.Values)
        {
            _ = Task.Run(async () =>
            {
                try { await client.SendTextAsync(json, _cts.Token).ConfigureAwait(false); } catch { }
            });
        }
    }

    private void OnStatusChanged(string sessionId, CliState state, string message)
    {
        if (!_endpoints.TryGetValue(sessionId, out var endpoint) || endpoint.Sockets.IsEmpty) return;

        var payload = new Models.TerminalStatusPayload("status", state == CliState.Running, message);
        string json = JsonSerializer.Serialize(payload);

        foreach (var client in endpoint.Sockets.Values)
        {
            _ = Task.Run(async () =>
            {
                try { await client.SendTextAsync(json, _cts.Token).ConfigureAwait(false); } catch { }
            });
        }
    }

    private void OnThemeChanged()
    {
        BroadcastTheme();
    }

    private void BroadcastTheme()
    {
        string theme = ThemeManager.CurrentTheme == AppTheme.Light ? "light" : "dark";
        string accent = ThemeManager.AccentHex;
        var payload = new { type = "theme_changed", theme, accentColor = accent };
        string json = JsonSerializer.Serialize(payload);

        foreach (var endpoint in _endpoints.Values)
        {
            foreach (var client in endpoint.Sockets.Values)
            {
                _ = Task.Run(async () =>
                {
                    try { await client.SendTextAsync(json, _cts.Token).ConfigureAwait(false); } catch { }
                });
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _cliManager.TerminalDataReceivedId -= OnTerminalDataReceived;
        _cliManager.StatusChangedId -= OnStatusChanged;
        ThemeManager.ThemeChanged -= OnThemeChanged;

        _cts.Cancel();

        foreach (var endpoint in _endpoints.Values)
        {
            StopEndpoint(endpoint);
        }
        _endpoints.Clear();

        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    [GeneratedRegex(@":root\s*\{[^}]*\}", RegexOptions.Singleline)]
    private static partial Regex MyRegex();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex AccentRegex();
}
