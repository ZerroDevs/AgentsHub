using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using AgentsHub.Helpers;

namespace AgentsHub.Services;

public class ConnectedClientInfo
{
    public string IpAddress { get; set; } = "";
    public string DeviceName { get; set; } = "Network Device";
    public string? HostName { get; set; }
    public string? UserAgent { get; set; }
    public int ActiveConnections { get; set; }
    public DateTime FirstSeen { get; set; } = DateTime.UtcNow;
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One socket the LAN feature tried to bind, and whether it came up.
/// </summary>
public sealed record LanForwarderInfo(int Port, string Label, string Address, bool Bound, string Detail);

public partial class LanProxyService : IDisposable
{
    private TcpListener? _listener;
    private readonly List<TcpListener> _secondaryListeners = [];
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private readonly Lock _lock = new();
    private readonly ConcurrentDictionary<string, DateTime> _recentlyNotifiedRemoteIps = new();
    private readonly ConcurrentDictionary<string, ConnectedClientInfo> _clients = new();

    /// <summary>
    /// Which sockets are actually bound for LAN access, recorded at bind time. Failures are
    /// included with <see cref="LanForwarderInfo.Bound"/> false so the UI can report a port that
    /// is not reachable instead of assuming every forwarder came up.
    /// </summary>
    private readonly List<LanForwarderInfo> _forwarders = [];

    public int ListenPort { get; set; } = 9191;
    public int TargetPort { get; set; } = 9119;
    public bool IsRunning { get; private set; }

    /// <summary>
    /// Pairing secret for the gateway. Loopback callers do not need it; every other device must
    /// arrive through a link or QR code that carries it. Rotated on each start.
    /// </summary>
    public string AccessToken { get; private set; } = string.Empty;

    /// <summary>Set when the last Start() could not bind. Null once a start succeeds.</summary>
    public string? LastBindError { get; private set; }

    public Func<Task>? StartHubAction { get; set; }
    public Func<Task<bool>>? CheckHubAliveAction { get; set; }

    /// <summary>
    /// The ports that may be offered for direct LAN exposure. The dashboard is not in this list:
    /// it is reached through the gateway port, which proxies to it already, so exposing the engine
    /// port a second time only duplicated the dashboard on an odd URL.
    /// </summary>
    public static int[] OfferedDirectPorts() => [9229, 9339];

    /// <summary>
    /// Nothing is forwarded until the user selects it. A direct forwarder is raw TCP and cannot
    /// carry the gateway's access token, so it is never on by default.
    /// </summary>
    public static int[] DefaultDirectPorts() => [];

    public static string LabelForPort(int port) => port switch
    {
        9229 => "Claude Code",
        9339 => "Antigravity",
        _ => $"Port {port}"
    };

    private const string DirectPortsPref = "LanDirectPorts";

    /// <summary>
    /// Which ports to expose, as the user left them. `none` is stored for an empty selection, so a
    /// deliberate "expose nothing" is not confused with "never configured".
    /// </summary>
    public static int[] ReadSelectedDirectPorts()
    {
        var raw = StartupHelper.GetStringPreference(DirectPortsPref, string.Empty);
        if (string.IsNullOrWhiteSpace(raw)) return DefaultDirectPorts();
        if (string.Equals(raw, "none", StringComparison.OrdinalIgnoreCase)) return [];

        var offered = OfferedDirectPorts();
        return [.. raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out var port) ? port : 0)
            // A selection saved before the port list changed can name a port that is no longer
            // offered; binding it would silently expose something the UI no longer shows.
            .Where(port => offered.Contains(port))
            .Distinct()];
    }

    public static void SaveSelectedDirectPorts(IEnumerable<int> ports)
    {
        var list = ports.Where(p => p is > 0 and <= 65535).Distinct().OrderBy(p => p).ToArray();
        StartupHelper.SetStringPreference(DirectPortsPref, list.Length == 0 ? "none" : string.Join(",", list));
    }

    public event Action<bool>? StateChanged;
    public event Action<string>? LogMessage;
    public event Action<string>? RemoteClientConnected;
    public event Action<ConnectedClientInfo>? RemoteDeviceConnected;
    public event Action<IReadOnlyList<ConnectedClientInfo>>? ActiveClientsChanged;

    public IReadOnlyList<LanForwarderInfo> GetForwarders()
    {
        lock (_lock)
        {
            return [.. _forwarders];
        }
    }

    public bool Start(int listenPort = 9191, int targetPort = 9119, IReadOnlyList<int>? directPorts = null)
    {
        lock (_lock)
        {
            if (IsRunning) return true;

            ListenPort = listenPort;
            TargetPort = targetPort;
            LastBindError = null;

            try
            {
                AccessToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                _cts = new CancellationTokenSource();
                _listener = new TcpListener(IPAddress.Any, listenPort);
                _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Start();

                IsRunning = true;
                _listenTask = Task.Run(() => AcceptConnectionsAsync(_listener, _cts.Token));

                LogMessage?.Invoke($"LAN Proxy active on 0.0.0.0:{listenPort} -> 127.0.0.1:{targetPort}");
                _forwarders.Add(new LanForwarderInfo(listenPort, "Gateway", "0.0.0.0 (all interfaces)", true, ""));

                // Only the ports the user chose are bound; an empty selection exposes nothing but
                // the gateway, which is what the gateway port alone is for.
                var wantedPorts = (directPorts ?? [])
                    .Where(port => port is > 0 and <= 65535)
                    .Distinct()
                    .ToArray();

                var lanIpStr = NetworkHelper.GetLocalIPv4();
                if (IPAddress.TryParse(lanIpStr, out var lanIp) && !IPAddress.IsLoopback(lanIp))
                {
                    foreach (var port in wantedPorts)
                    {
                        BindDirectForwarder(lanIp, port, port, LabelForPort(port));
                    }
                }
                else
                {
                    LastBindError = "No LAN IPv4 address was found, so only the gateway port is available.";
                }

                StateChanged?.Invoke(true);
                return true;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"Failed to bind proxy listener on port {listenPort}: {ex.Message}");
                LastBindError = ex.Message;
                Stop();
                LastBindError = ex.Message;
                return false;
            }
        }
    }

    private void BindDirectForwarder(IPAddress ip, int listenPort, int targetPort, string label)
    {
        bool isHub = listenPort == targetPort;
        try
        {
            var l = new TcpListener(ip, listenPort);
            l.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            l.Start();
            _secondaryListeners.Add(l);

            if (_cts != null)
            {
                if (isHub)
                {
                    _ = Task.Run(() => AcceptConnectionsAsync(l, _cts.Token));
                }
                else
                {
                    _ = Task.Run(() => AcceptForwardConnectionAsync(l, targetPort, _cts.Token));
                }
            }
            LogMessage?.Invoke($"LAN Direct Forwarder active on {ip}:{listenPort} -> 127.0.0.1:{targetPort}");
            _forwarders.Add(new LanForwarderInfo(listenPort, label, ip.ToString(), true, ""));
        }
        catch (Exception ex)
        {
            // Ports already claimed by another process land here. Record it rather than
            // letting the UI imply this port is reachable from the network.
            LogMessage?.Invoke($"Direct LAN forwarder notice on {ip}:{listenPort}: {ex.Message}");
            _forwarders.Add(new LanForwarderInfo(listenPort, label, ip.ToString(), false, ex.Message));
        }
    }

    private static async Task AcceptForwardConnectionAsync(TcpListener listener, int targetLoopbackPort, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(token);
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        using var backend = new TcpClient();
                        try
                        {
                            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                            await backend.ConnectAsync(IPAddress.Loopback, targetLoopbackPort, connectCts.Token);
                            using var clientCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                            var cStream = client.GetStream();
                            var bStream = backend.GetStream();
                            var t1 = cStream.CopyToAsync(bStream, clientCts.Token);
                            var t2 = bStream.CopyToAsync(cStream, clientCts.Token);
                            await Task.WhenAny(t1, t2);
                            clientCts.Cancel();
                        }
                        catch { }
                    }
                }, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch { }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!IsRunning && _listener == null) return;

            IsRunning = false;

            try
            {
                _cts?.Cancel();
            }
            catch
            {
                // Suppress cancellation exceptions
            }

            try
            {
                _listener?.Stop();
            }
            catch
            {
                // Suppress socket stop exceptions
            }

            foreach (var l in _secondaryListeners)
            {
                try { l.Stop(); } catch { }
            }
            _secondaryListeners.Clear();
            _forwarders.Clear();

            _listener = null;
            _cts?.Dispose();
            _cts = null;

            _clients.Clear();
            AccessToken = string.Empty;
            NotifyClientsChanged();

            LogMessage?.Invoke("LAN Proxy stopped.");
            StateChanged?.Invoke(false);
        }
    }

    public IReadOnlyList<ConnectedClientInfo> GetActiveClients()
    {
        var now = DateTime.UtcNow;
        return [.. _clients.Values
            .Where(c => c.ActiveConnections > 0 || (now - c.LastActivity).TotalSeconds < 90)
            .OrderByDescending(c => c.ActiveConnections > 0)
            .ThenByDescending(c => c.LastActivity)];
    }

    public string GetConnectedClientsSummary()
    {
        var active = GetActiveClients();
        if (active.Count == 0)
        {
            return "Active Clients: 0 (No remote devices)";
        }

        var items = active.Select(c => $"{c.IpAddress} [{c.DeviceName}]");
        return $"Active Clients: {active.Count} • {string.Join(", ", items)}";
    }

    private void NotifyClientsChanged()
    {
        ActiveClientsChanged?.Invoke(GetActiveClients());
    }

    private void RegisterClientConnection(string ip)
    {
        var client = _clients.GetOrAdd(ip, address =>
        {
            var info = new ConnectedClientInfo
            {
                IpAddress = address,
                DeviceName = "Network Device",
                FirstSeen = DateTime.UtcNow,
                LastActivity = DateTime.UtcNow,
                ActiveConnections = 0
            };
            _ = TryResolveHostNameAsync(address);
            return info;
        });

        lock (client)
        {
            client.ActiveConnections++;
            client.LastActivity = DateTime.UtcNow;
        }

        NotifyRemoteClientIfApplicable(client);
        NotifyClientsChanged();
    }

    private void UnregisterClientConnection(string ip)
    {
        if (_clients.TryGetValue(ip, out var client))
        {
            lock (client)
            {
                client.ActiveConnections = Math.Max(0, client.ActiveConnections - 1);
                client.LastActivity = DateTime.UtcNow;
            }
            NotifyClientsChanged();
        }
    }

    private void UpdateClientUserAgent(string ip, string userAgent)
    {
        if (_clients.TryGetValue(ip, out var client))
        {
            var updated = false;
            lock (client)
            {
                if (client.UserAgent != userAgent)
                {
                    client.UserAgent = userAgent;
                    client.DeviceName = CombineHostAndDevice(client.HostName, userAgent);
                    updated = true;
                }
            }
            if (updated)
            {
                NotifyClientsChanged();
            }
        }
    }

    private async Task TryResolveHostNameAsync(string ip)
    {
        try
        {
            using var cts = new CancellationTokenSource(800);
            var entry = await Dns.GetHostEntryAsync(ip, cts.Token);
            if (!string.IsNullOrWhiteSpace(entry.HostName) && entry.HostName != ip)
            {
                var clean = entry.HostName.Split('.')[0];
                if (_clients.TryGetValue(ip, out var client))
                {
                    lock (client)
                    {
                        client.HostName = clean;
                        client.DeviceName = CombineHostAndDevice(clean, client.UserAgent);
                    }
                    NotifyClientsChanged();
                }
            }
        }
        catch
        {
            // Reverse DNS timeout or unresolvable on local subnet
        }
    }

    private static string CombineHostAndDevice(string? hostName, string? userAgent)
    {
        var deviceType = DetectDeviceType(userAgent);
        if (!string.IsNullOrWhiteSpace(hostName) && !hostName.Equals(deviceType, StringComparison.OrdinalIgnoreCase))
        {
            return $"{hostName} ({deviceType})";
        }
        return deviceType;
    }

    public static string DetectDeviceType(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return "Network Device";

        if (userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase)) return "Apple iPhone";
        if (userAgent.Contains("iPad", StringComparison.OrdinalIgnoreCase)) return "Apple iPad";
        if (userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase))
        {
            if (userAgent.Contains("Samsung", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("SM-", StringComparison.OrdinalIgnoreCase))
                return "Samsung Galaxy";
            if (userAgent.Contains("Pixel", StringComparison.OrdinalIgnoreCase))
                return "Google Pixel";
            if (userAgent.Contains("Xiaomi", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("Redmi", StringComparison.OrdinalIgnoreCase))
                return "Xiaomi Phone";
            return "Android Device";
        }
        if (userAgent.Contains("Windows NT", StringComparison.OrdinalIgnoreCase)) return "Windows PC";
        if (userAgent.Contains("Macintosh", StringComparison.OrdinalIgnoreCase)) return "Mac";
        if (userAgent.Contains("Linux", StringComparison.OrdinalIgnoreCase)) return "Linux PC";
        if (userAgent.Contains("curl", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("python", StringComparison.OrdinalIgnoreCase))
            return "API Client";

        return "Remote Device";
    }

    private static string? ExtractUserAgent(string headers)
    {
        var match = MyRegex().Match(headers);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private async Task AcceptConnectionsAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(token);
                _ = Task.Run(() => HandleClientAsync(client, token), token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"Proxy accept warning: {ex.Message}");
            }
        }
    }

    private void NotifyRemoteClientIfApplicable(ConnectedClientInfo client)
    {
        try
        {
            var ipStr = client.IpAddress;
            var now = DateTime.UtcNow;

            // Throttle notifications to once per 2 minutes per remote IP
            if (!_recentlyNotifiedRemoteIps.TryGetValue(ipStr, out var lastTime) ||
                (now - lastTime).TotalMinutes >= 2)
            {
                _recentlyNotifiedRemoteIps[ipStr] = now;
                RemoteClientConnected?.Invoke(ipStr);
                RemoteDeviceConnected?.Invoke(client);
            }
        }
        catch
        {
            // Ignore socket endpoint interrogation failures
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        string clientIp = "127.0.0.1";
        bool isRemote = false;

        try
        {
            if (client.Client.RemoteEndPoint is IPEndPoint endpoint)
            {
                clientIp = endpoint.Address.ToString();
                isRemote = !IPAddress.IsLoopback(endpoint.Address);
            }
        }
        catch
        {
            // Endpoint interrogation issue
        }

        if (isRemote)
        {
            RegisterClientConnection(clientIp);
        }

        using (client)
        {
            TcpClient? backend = null;
            try
            {
                using var clientCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                var clientStream = client.GetStream();

                var buffer = new byte[8192];
                var bytesRead = await clientStream.ReadAsync(buffer.AsMemory(0, buffer.Length), clientCts.Token);
                if (bytesRead <= 0) return;

                var initialText = Encoding.ASCII.GetString(buffer, 0, Math.Min(bytesRead, 1024));
                var requestPath = ExtractRequestPath(initialText);

                // A remote device needs the pairing token from the QR link. A loopback caller using
                // any other Host name does too, which is what a DNS-rebinding page looks like.
                bool needsToken = isRemote || !HostHeaderIsLocal(initialText);
                if (needsToken)
                {
                    // Fail closed: no token means no remote caller gets through, even if startup
                    // somehow left the token unset.
                    if (string.IsNullOrEmpty(AccessToken))
                    {
                        await SendPairingRequiredAsync(clientStream, requestPath);
                        return;
                    }

                    string? supplied = ExtractQueryValue(requestPath, "k")
                        ?? ExtractCookieValue(initialText, "agentshub_lan");

                    if (!TokenEquals(supplied, AccessToken))
                    {
                        await SendPairingRequiredAsync(clientStream, requestPath);
                        return;
                    }

                    // Move the token out of the URL so it stops appearing in history and logs.
                    if (ExtractQueryValue(requestPath, "k") != null)
                    {
                        await SendTokenCookieRedirectAsync(clientStream, requestPath);
                        return;
                    }
                }

                if (isRemote)
                {
                    var userAgent = ExtractUserAgent(initialText);
                    if (!string.IsNullOrEmpty(userAgent))
                    {
                        UpdateClientUserAgent(clientIp, userAgent);
                    }
                }

                // Handle internal proxy control endpoints
                if (requestPath.StartsWith("/start", StringComparison.OrdinalIgnoreCase) ||
                    requestPath.StartsWith("/__hub/start", StringComparison.OrdinalIgnoreCase))
                {
                    if (StartHubAction != null)
                    {
                        _ = StartHubAction.Invoke();
                    }
                    await SendStartingPageAsync(clientStream, requestPath);
                    return;
                }

                if (requestPath.StartsWith("/status", StringComparison.OrdinalIgnoreCase) ||
                    requestPath.StartsWith("/__hub/status", StringComparison.OrdinalIgnoreCase))
                {
                    var isAlive = await CheckBackendAliveAsync(TargetPort);
                    await SendStatusJsonAsync(clientStream, isAlive);
                    return;
                }

                // Verify if backend is reachable
                var backendHealthy = await CheckBackendAliveAsync(TargetPort);
                if (!backendHealthy)
                {
                    await SendOfflinePageAsync(clientStream, requestPath);
                    return;
                }

                // Connect to backend daemon
                backend = new TcpClient();
                try
                {
                    using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await backend.ConnectAsync(IPAddress.Loopback, TargetPort, connectCts.Token);
                }
                catch
                {
                    await SendOfflinePageAsync(clientStream, requestPath);
                    return;
                }

                var backendStream = backend.GetStream();

                // Forward the initial modified request
                var modifiedInitialBytes = ProcessClientBuffer(buffer, bytesRead, TargetPort);
                await backendStream.WriteAsync(modifiedInitialBytes.AsMemory(), clientCts.Token);
                await backendStream.FlushAsync(clientCts.Token);

                // Stream bidirectionally with continuous header adjustment on keep-alive
                var clientToBackend = ForwardClientToBackendAsync(clientStream, backendStream, TargetPort, clientCts.Token);
                var backendToClient = StreamCopyAsync(backendStream, clientStream, clientCts.Token);

                await Task.WhenAny(clientToBackend, backendToClient);
                clientCts.Cancel();
            }
            catch
            {
                // Normal connection teardown
            }
            finally
            {
                if (isRemote)
                {
                    UnregisterClientConnection(clientIp);
                }
                backend?.Dispose();
            }
        }
    }

    private static string ExtractRequestPath(string text)
    {
        var firstLineEnd = text.IndexOfAny(['\r', '\n']);
        var line = firstLineEnd > 0 ? text[..firstLineEnd] : text;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? parts[1] : "/";
    }

    private async Task<bool> CheckBackendAliveAsync(int port)
    {
        if (CheckHubAliveAction != null)
        {
            return await CheckHubAliveAction.Invoke();
        }

        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(400);
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static async Task ForwardClientToBackendAsync(Stream clientStream, Stream backendStream, int targetPort, CancellationToken token)
    {
        var buffer = new byte[16384];
        while (!token.IsCancellationRequested)
        {
            var bytesRead = await clientStream.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
            if (bytesRead <= 0) break;

            var modifiedBytes = ProcessClientBuffer(buffer, bytesRead, targetPort);
            await backendStream.WriteAsync(modifiedBytes.AsMemory(), token);
            await backendStream.FlushAsync(token);
        }
    }

    private static byte[] ProcessClientBuffer(byte[] buffer, int length, int targetPort)
    {
        try
        {
            if (StartsWithHttpMethod(buffer, length))
            {
                var text = Encoding.ASCII.GetString(buffer, 0, length);
                var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);

                if (headerEnd > 0)
                {
                    var headerSection = text[..headerEnd];
                    var bodySection = buffer[(headerEnd + 4)..length];

                    // Replace Host line with target while strictly keeping CRLF intact
                    var rewrittenHeaders = Regex.Replace(
                        headerSection,
                        @"(?im)^Host:[^\r\n]*",
                        $"Host: 127.0.0.1:{targetPort}"
                    );

                    var newHeaderBytes = Encoding.ASCII.GetBytes(rewrittenHeaders + "\r\n\r\n");
                    var result = new byte[newHeaderBytes.Length + bodySection.Length];
                    Buffer.BlockCopy(newHeaderBytes, 0, result, 0, newHeaderBytes.Length);
                    Buffer.BlockCopy(bodySection, 0, result, newHeaderBytes.Length, bodySection.Length);
                    return result;
                }
            }
        }
        catch
        {
            // Fall back to original bytes on format parse issues
        }

        var fallback = new byte[length];
        Buffer.BlockCopy(buffer, 0, fallback, 0, length);
        return fallback;
    }

    private static bool StartsWithHttpMethod(byte[] buffer, int length)
    {
        if (length < 4) return false;
        return (buffer[0] == 'G' && buffer[1] == 'E' && buffer[2] == 'T' && buffer[3] == ' ') ||
               (buffer[0] == 'P' && buffer[1] == 'O' && buffer[2] == 'S' && buffer[3] == 'T') ||
               (buffer[0] == 'P' && buffer[1] == 'U' && buffer[2] == 'T' && buffer[3] == ' ') ||
               (buffer[0] == 'D' && buffer[1] == 'E' && buffer[2] == 'L' && buffer[3] == 'E') ||
               (buffer[0] == 'P' && buffer[1] == 'A' && buffer[2] == 'T' && buffer[3] == 'C') ||
               (buffer[0] == 'H' && buffer[1] == 'E' && buffer[2] == 'A' && buffer[3] == 'D') ||
               (buffer[0] == 'O' && buffer[1] == 'P' && buffer[2] == 'T' && buffer[3] == 'I');
    }

    private static async Task StreamCopyAsync(Stream source, Stream destination, CancellationToken token)
    {
        var buffer = new byte[16384];
        while (!token.IsCancellationRequested)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
            if (read <= 0) break;
            await destination.WriteAsync(buffer.AsMemory(0, read), token);
            await destination.FlushAsync(token);
        }
    }

    private record LanThemeTokens(
        string BgApp,
        string BgCard,
        string BorderSubtle,
        string TextMain,
        string TextMuted,
        string TextDim,
        string Accent,
        string AccentText,
        string BtnOpenBg,
        string BtnOpenText,
        string Shadow
    );

    private static LanThemeTokens GetLanThemeTokens(string? requestPath = null)
    {
        bool isLight = ThemeManager.CurrentTheme == AppTheme.Light;
        string accent = ThemeManager.AccentHex;

        if (!string.IsNullOrEmpty(requestPath) && requestPath.Contains('?'))
        {
            var qs = requestPath[(requestPath.IndexOf('?') + 1)..];
            foreach (var part in qs.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2)
                {
                    if (kv[0].Equals("theme", StringComparison.OrdinalIgnoreCase))
                    {
                        isLight = kv[1].Equals("light", StringComparison.OrdinalIgnoreCase);
                    }
                    else if (kv[0].Equals("accent", StringComparison.OrdinalIgnoreCase))
                    {
                        var decoded = Uri.UnescapeDataString(kv[1]);
                        if (!string.IsNullOrWhiteSpace(decoded)) accent = decoded;
                    }
                }
            }
        }

        var (h, _, _) = ThemeManager.HexToHsl(accent);
        double lum = ThemeManager.GetLuminance(accent);
        string accentText = lum > 0.62 ? "#0a0f14" : "#ffffff";

        if (isLight)
        {
            return new LanThemeTokens(
                BgApp: $"hsl({h}, 22%, 95.5%)",
                BgCard: "#ffffff",
                BorderSubtle: $"hsl({h}, 25%, 83%)",
                TextMain: $"hsl({h}, 28%, 11%)",
                TextMuted: $"hsl({h}, 16%, 36%)",
                TextDim: $"hsl({h}, 10%, 52%)",
                Accent: accent,
                AccentText: accentText,
                BtnOpenBg: "#ffffff",
                BtnOpenText: $"hsl({h}, 28%, 11%)",
                Shadow: "0 10px 25px rgba(0,0,0,0.08)"
            );
        }
        else
        {
            return new LanThemeTokens(
                BgApp: $"hsl({h}, 22%, 5.5%)",
                BgCard: $"hsl({h}, 26%, 12.5%)",
                BorderSubtle: $"hsl({h}, 24%, 19%)",
                TextMain: "#f8fafc",
                TextMuted: $"hsl({h}, 12%, 68%)",
                TextDim: $"hsl({h}, 10%, 48%)",
                Accent: accent,
                AccentText: "#ffffff",
                BtnOpenBg: $"hsl({h}, 26%, 16%)",
                BtnOpenText: "#f8fafc",
                Shadow: "0 10px 25px rgba(0,0,0,0.5)"
            );
        }
    }

    private static string? ExtractQueryValue(string requestPath, string key)
    {
        int question = requestPath.IndexOf('?');
        if (question < 0) return null;

        foreach (string part in requestPath[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = part.IndexOf('=');
            if (equals <= 0) continue;

            if (string.Equals(part[..equals], key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(part[(equals + 1)..]);
            }
        }

        return null;
    }

    /// <summary>
    /// True when the request arrived for this machine's own name. A browser tricked into resolving
    /// an attacker domain to 127.0.0.1 still sends that domain in Host, so it is not local here.
    /// </summary>
    private static bool HostHeaderIsLocal(string headers)
    {
        foreach (string line in headers.Split('\n'))
        {
            if (!line.TrimStart().StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) continue;

            string host = line[(line.IndexOf(':') + 1)..].Trim();
            int colon = host.LastIndexOf(':');
            if (colon > 0) host = host[..colon];
            host = host.Trim('[', ']');

            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
            return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
        }

        return false;
    }

    private static string? ExtractCookieValue(string headers, string name)
    {
        foreach (string line in headers.Split('\n'))
        {
            if (!line.TrimStart().StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (string cookie in line[(line.IndexOf(':') + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = cookie.Trim();
                int equals = trimmed.IndexOf('=');
                if (equals <= 0) continue;

                if (string.Equals(trimmed[..equals], name, StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed[(equals + 1)..];
                }
            }
        }

        return null;
    }

    private static bool TokenEquals(string? supplied, string expected)
    {
        if (string.IsNullOrEmpty(supplied) || expected.Length == 0) return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(supplied),
            Encoding.UTF8.GetBytes(expected));
    }

    private static string RemoveQueryValue(string requestPath, string key)
    {
        int question = requestPath.IndexOf('?');
        if (question < 0) return requestPath;

        string path = requestPath[..question];
        var kept = new List<string>();

        foreach (string part in requestPath[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = part.IndexOf('=');
            string partKey = equals > 0 ? part[..equals] : part;
            if (string.Equals(partKey, key, StringComparison.OrdinalIgnoreCase)) continue;
            kept.Add(part);
        }

        return kept.Count == 0 ? path : $"{path}?{string.Join('&', kept)}";
    }

    private async Task SendTokenCookieRedirectAsync(Stream stream, string requestPath)
    {
        string location = RemoveQueryValue(requestPath, "k");
        string header = $"HTTP/1.1 302 Found\r\nLocation: {location}\r\nSet-Cookie: agentshub_lan={AccessToken}; HttpOnly; SameSite=Lax; Path=/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(header);

        await stream.WriteAsync(headerBytes.AsMemory());
        await stream.FlushAsync();
    }

    private static async Task SendPairingRequiredAsync(Stream stream, string requestPath)
    {
        var t = GetLanThemeTokens(requestPath);
        string html = $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1.0">
              <title>Agents Hub - Pairing Required</title>
              <style>
                :root { --bg-app: {{t.BgApp}}; --bg-card: {{t.BgCard}}; --border-subtle: {{t.BorderSubtle}}; --text-main: {{t.TextMain}}; --text-muted: {{t.TextMuted}}; --accent: {{t.Accent}}; --accent-text: {{t.AccentText}}; }
                * { box-sizing: border-box; }
                body { background: var(--bg-app); color: var(--text-main); font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; display: flex; align-items: center; justify-content: center; min-height: 100vh; margin: 0; padding: 16px; }
                .card { background: var(--bg-card); border: 1px solid var(--border-subtle); border-radius: 12px; padding: 36px 28px; max-width: 460px; width: 100%; text-align: center; }
                h1 { font-size: 1.4rem; font-weight: 700; margin: 0 0 10px 0; }
                p { color: var(--text-muted); font-size: 0.95rem; line-height: 1.5; margin: 0; }
                .hint { margin-top: 18px; padding: 12px; border: 1px dashed var(--border-subtle); border-radius: 8px; color: var(--accent); font-size: 0.85rem; }
              </style>
            </head>
            <body>
              <div class="card">
                <h1>Pair this device first</h1>
                <p>This gateway only answers a device that arrived from the LAN link or QR code shown in Agents Hub.</p>
                <div class="hint">Open Settings, then LAN Remote Proxy, and scan the code again.</div>
              </div>
            </body>
            </html>
            """;

        byte[] bodyBytes = Encoding.UTF8.GetBytes(html);
        string header = $"HTTP/1.1 401 Unauthorized\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(header);

        await stream.WriteAsync(headerBytes.AsMemory());
        await stream.WriteAsync(bodyBytes.AsMemory());
        await stream.FlushAsync();
    }

    private static async Task SendOfflinePageAsync(Stream stream, string? requestPath = null)
    {
        var t = GetLanThemeTokens(requestPath);
        string html = $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1.0">
              <title>Agents Hub - Engine Offline</title>
              <style>
                :root {
                  --bg-app: {{t.BgApp}};
                  --bg-card: {{t.BgCard}};
                  --border-subtle: {{t.BorderSubtle}};
                  --text-main: {{t.TextMain}};
                  --text-muted: {{t.TextMuted}};
                  --text-dim: {{t.TextDim}};
                  --accent: {{t.Accent}};
                  --accent-text: {{t.AccentText}};
                  --btn-open-bg: {{t.BtnOpenBg}};
                  --btn-open-text: {{t.BtnOpenText}};
                  --shadow: {{t.Shadow}};
                }
                * { box-sizing: border-box; }
                body { background: var(--bg-app); color: var(--text-main); font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; display: flex; align-items: center; justify-content: center; min-height: 100vh; margin: 0; padding: 16px; transition: background 0.2s ease, color 0.2s ease; }
                .card { background: var(--bg-card); border: 1px solid var(--border-subtle); border-radius: 12px; padding: 36px 28px; max-width: 460px; width: 100%; text-align: center; box-shadow: var(--shadow); transition: background 0.2s ease, border-color 0.2s ease; }
                .badge { display: inline-block; background: rgba(239, 68, 68, 0.15); color: #ef4444; border: 1px solid rgba(239, 68, 68, 0.3); padding: 4px 12px; border-radius: 999px; font-size: 0.8rem; font-weight: 600; text-transform: uppercase; letter-spacing: 0.05em; margin-bottom: 16px; }
                h1 { font-size: 1.4rem; font-weight: 700; margin: 0 0 10px 0; color: var(--text-main); }
                p { color: var(--text-muted); font-size: 0.95rem; line-height: 1.5; margin: 0 0 24px 0; }
                .btn-group { display: flex; flex-direction: column; gap: 10px; }
                .btn { display: block; width: 100%; padding: 12px 18px; border-radius: 8px; font-size: 0.95rem; font-weight: 600; text-decoration: none; cursor: pointer; transition: all 0.2s ease; border: none; text-align: center; }
                .btn-start { background: var(--accent); color: var(--accent-text); }
                .btn-start:hover { filter: brightness(1.08); transform: translateY(-1px); }
                .btn-open { background: var(--btn-open-bg); color: var(--btn-open-text); border: 1px solid var(--border-subtle); }
                .btn-open:hover { filter: brightness(0.96); }
                .progress { display: none; margin-top: 20px; padding: 16px; background: var(--bg-app); border: 1px solid var(--border-subtle); border-radius: 8px; }
                .spinner { width: 28px; height: 28px; border: 3px solid rgba(64, 73, 185, 0.2); border-top-color: var(--accent); border-radius: 50%; animation: spin 0.8s linear infinite; margin: 0 auto 12px auto; }
                @keyframes spin { to { transform: rotate(360deg); } }
                .status-msg { font-size: 0.9rem; color: var(--accent); font-weight: 600; margin: 0 0 4px 0; }
                .status-sub { font-size: 0.8rem; color: var(--text-dim); margin: 0; }
              </style>
            </head>
            <body>
              <div class="card">
                <div class="badge">Engine Offline</div>
                <h1>Hub Engine Offline</h1>
                <p>The local Hub daemon on port 9119 is stopped. You can launch it directly from this web page.</p>
                <div class="btn-group" id="actions">
                  <button type="button" class="btn btn-start" onclick="triggerStart()">Start Hub Engine</button>
                  <a href="/" class="btn btn-open">Open Dashboard (Retry)</a>
                </div>
                <div class="progress" id="progressArea">
                  <div class="spinner"></div>
                  <p class="status-msg" id="statusMsg">Initiating Hub Engine...</p>
                  <p class="status-sub">Waiting for port 9119 to become ready. Redirecting automatically.</p>
                </div>
              </div>
              <script>
                function hexToHsl(hex) {
                  hex = (hex || '').replace(/^#/, '').trim();
                  if (hex.length === 3) hex = hex.split('').map(c => c + c).join('');
                  if (hex.length !== 6) return { h: 236, s: 49, l: 49 };
                  const r = parseInt(hex.substring(0, 2), 16) / 255;
                  const g = parseInt(hex.substring(2, 4), 16) / 255;
                  const b = parseInt(hex.substring(4, 6), 16) / 255;
                  const max = Math.max(r, g, b), min = Math.min(r, g, b);
                  let h = 0, s = 0, l = (max + min) / 2;
                  if (max !== min) {
                    const d = max - min;
                    s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
                    switch (max) {
                      case r: h = (g - b) / d + (g < b ? 6 : 0); break;
                      case g: h = (b - r) / d + 2; break;
                      case b: h = (r - g) / d + 4; break;
                    }
                    h = Math.round(h * 60);
                  }
                  return { h, s: Math.round(s * 100), l: Math.round(l * 100) };
                }
                function applyLanTheme(themeName, accentHex) {
                  const isLight = themeName === 'light';
                  const hex = accentHex || '#4049B9';
                  const hsl = hexToHsl(hex);
                  const h = hsl.h, s = hsl.s, l = hsl.l;
                  const root = document.documentElement.style;
                  if (isLight) {
                    root.setProperty('--bg-app', 'hsl(' + h + ', 22%, 95.5%)');
                    root.setProperty('--bg-card', '#ffffff');
                    root.setProperty('--border-subtle', 'hsl(' + h + ', 25%, 83%)');
                    root.setProperty('--text-main', 'hsl(' + h + ', 28%, 11%)');
                    root.setProperty('--text-muted', 'hsl(' + h + ', 16%, 36%)');
                    root.setProperty('--text-dim', 'hsl(' + h + ', 10%, 52%)');
                    root.setProperty('--accent', hex);
                    root.setProperty('--accent-text', l > 55 ? '#0a0f14' : '#ffffff');
                    root.setProperty('--btn-open-bg', '#ffffff');
                    root.setProperty('--btn-open-text', 'hsl(' + h + ', 28%, 11%)');
                    root.setProperty('--shadow', '0 10px 25px rgba(0,0,0,0.08)');
                  } else {
                    root.setProperty('--bg-app', 'hsl(' + h + ', 22%, 5.5%)');
                    root.setProperty('--bg-card', 'hsl(' + h + ', 26%, 12.5%)');
                    root.setProperty('--border-subtle', 'hsl(' + h + ', 24%, 19%)');
                    root.setProperty('--text-main', '#f8fafc');
                    root.setProperty('--text-muted', 'hsl(' + h + ', 12%, 68%)');
                    root.setProperty('--text-dim', 'hsl(' + h + ', 10%, 48%)');
                    root.setProperty('--accent', hex);
                    root.setProperty('--accent-text', '#ffffff');
                    root.setProperty('--btn-open-bg', 'hsl(' + h + ', 26%, 16%)');
                    root.setProperty('--btn-open-text', '#f8fafc');
                    root.setProperty('--shadow', '0 10px 25px rgba(0,0,0,0.5)');
                  }
                }

                let isPolling = false;
                function triggerStart() {
                  document.getElementById('actions').style.display = 'none';
                  document.getElementById('progressArea').style.display = 'block';
                  fetch('/__hub/start').catch(() => {});
                  startPolling();
                }
                function startPolling() {
                  if (isPolling) return;
                  isPolling = true;
                  const interval = setInterval(async () => {
                    try {
                      const res = await fetch('/__hub/status');
                      if (res.ok) {
                        const data = await res.json();
                        if (data.theme) applyLanTheme(data.theme, data.accent);
                        if (data.online) {
                          clearInterval(interval);
                          document.getElementById('statusMsg').innerText = 'Hub Online! Loading...';
                          setTimeout(() => { window.location.href = '/'; }, 600);
                        }
                      }
                    } catch (e) {}
                  }, 1200);
                }
                setInterval(async () => {
                  if (isPolling) return;
                  try {
                    const res = await fetch('/__hub/status');
                    if (res.ok) {
                      const data = await res.json();
                      if (data.theme) applyLanTheme(data.theme, data.accent);
                      if (data.online) {
                        window.location.href = '/';
                      }
                    }
                  } catch (e) {}
                }, 2500);
              </script>
            </body>
            </html>
            """;

        var bodyBytes = Encoding.UTF8.GetBytes(html);
        var header = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);

        await stream.WriteAsync(headerBytes.AsMemory());
        await stream.WriteAsync(bodyBytes.AsMemory());
        await stream.FlushAsync();
    }

    private static async Task SendStartingPageAsync(Stream stream, string? requestPath = null)
    {
        var t = GetLanThemeTokens(requestPath);
        string html = $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1.0">
              <title>Starting Hub Engine</title>
              <style>
                :root {
                  --bg-app: {{t.BgApp}};
                  --bg-card: {{t.BgCard}};
                  --border-subtle: {{t.BorderSubtle}};
                  --text-main: {{t.TextMain}};
                  --text-muted: {{t.TextMuted}};
                  --text-dim: {{t.TextDim}};
                  --accent: {{t.Accent}};
                  --accent-text: {{t.AccentText}};
                  --btn-open-bg: {{t.BtnOpenBg}};
                  --btn-open-text: {{t.BtnOpenText}};
                  --shadow: {{t.Shadow}};
                }
                * { box-sizing: border-box; }
                body { background: var(--bg-app); color: var(--text-main); font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; display: flex; align-items: center; justify-content: center; min-height: 100vh; margin: 0; padding: 16px; transition: background 0.2s ease, color 0.2s ease; }
                .card { background: var(--bg-card); border: 1px solid var(--border-subtle); border-radius: 12px; padding: 36px 28px; max-width: 460px; width: 100%; text-align: center; box-shadow: var(--shadow); transition: background 0.2s ease, border-color 0.2s ease; }
                .spinner { width: 34px; height: 34px; border: 3px solid rgba(64, 73, 185, 0.2); border-top-color: var(--accent); border-radius: 50%; animation: spin 0.8s linear infinite; margin: 0 auto 16px auto; }
                @keyframes spin { to { transform: rotate(360deg); } }
                h1 { font-size: 1.35rem; font-weight: 700; margin: 0 0 10px 0; color: var(--accent); }
                p { color: var(--text-muted); font-size: 0.95rem; line-height: 1.5; margin: 0 0 20px 0; }
                .btn { display: inline-block; padding: 10px 20px; border-radius: 8px; font-size: 0.9rem; font-weight: 600; text-decoration: none; background: var(--btn-open-bg); color: var(--btn-open-text); border: 1px solid var(--border-subtle); }
                .btn:hover { filter: brightness(0.96); }
              </style>
            </head>
            <body>
              <div class="card">
                <div class="spinner"></div>
                <h1>Starting Hub Engine...</h1>
                <p>Background process has been launched. Waiting for local port 9119 to respond.</p>
                <a href="/" class="btn">Open Hub Dashboard</a>
              </div>
              <script>
                function hexToHsl(hex) {
                  hex = (hex || '').replace(/^#/, '').trim();
                  if (hex.length === 3) hex = hex.split('').map(c => c + c).join('');
                  if (hex.length !== 6) return { h: 236, s: 49, l: 49 };
                  const r = parseInt(hex.substring(0, 2), 16) / 255;
                  const g = parseInt(hex.substring(2, 4), 16) / 255;
                  const b = parseInt(hex.substring(4, 6), 16) / 255;
                  const max = Math.max(r, g, b), min = Math.min(r, g, b);
                  let h = 0, s = 0, l = (max + min) / 2;
                  if (max !== min) {
                    const d = max - min;
                    s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
                    switch (max) {
                      case r: h = (g - b) / d + (g < b ? 6 : 0); break;
                      case g: h = (b - r) / d + 2; break;
                      case b: h = (r - g) / d + 4; break;
                    }
                    h = Math.round(h * 60);
                  }
                  return { h, s: Math.round(s * 100), l: Math.round(l * 100) };
                }
                function applyLanTheme(themeName, accentHex) {
                  const isLight = themeName === 'light';
                  const hex = accentHex || '#4049B9';
                  const hsl = hexToHsl(hex);
                  const h = hsl.h, s = hsl.s, l = hsl.l;
                  const root = document.documentElement.style;
                  if (isLight) {
                    root.setProperty('--bg-app', 'hsl(' + h + ', 22%, 95.5%)');
                    root.setProperty('--bg-card', '#ffffff');
                    root.setProperty('--border-subtle', 'hsl(' + h + ', 25%, 83%)');
                    root.setProperty('--text-main', 'hsl(' + h + ', 28%, 11%)');
                    root.setProperty('--text-muted', 'hsl(' + h + ', 16%, 36%)');
                    root.setProperty('--text-dim', 'hsl(' + h + ', 10%, 52%)');
                    root.setProperty('--accent', hex);
                    root.setProperty('--accent-text', l > 55 ? '#0a0f14' : '#ffffff');
                    root.setProperty('--btn-open-bg', '#ffffff');
                    root.setProperty('--btn-open-text', 'hsl(' + h + ', 28%, 11%)');
                    root.setProperty('--shadow', '0 10px 25px rgba(0,0,0,0.08)');
                  } else {
                    root.setProperty('--bg-app', 'hsl(' + h + ', 22%, 5.5%)');
                    root.setProperty('--bg-card', 'hsl(' + h + ', 26%, 12.5%)');
                    root.setProperty('--border-subtle', 'hsl(' + h + ', 24%, 19%)');
                    root.setProperty('--text-main', '#f8fafc');
                    root.setProperty('--text-muted', 'hsl(' + h + ', 12%, 68%)');
                    root.setProperty('--text-dim', 'hsl(' + h + ', 10%, 48%)');
                    root.setProperty('--accent', hex);
                    root.setProperty('--accent-text', '#ffffff');
                    root.setProperty('--btn-open-bg', 'hsl(' + h + ', 26%, 16%)');
                    root.setProperty('--btn-open-text', '#f8fafc');
                    root.setProperty('--shadow', '0 10px 25px rgba(0,0,0,0.5)');
                  }
                }
                const interval = setInterval(async () => {
                  try {
                    const res = await fetch('/__hub/status');
                    if (res.ok) {
                      const data = await res.json();
                      if (data.theme) applyLanTheme(data.theme, data.accent);
                      if (data.online) {
                        clearInterval(interval);
                        setTimeout(() => { window.location.href = '/'; }, 500);
                      }
                    }
                  } catch (e) {}
                }, 1000);
              </script>
            </body>
            </html>
            """;

        var bodyBytes = Encoding.UTF8.GetBytes(html);
        var header = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);

        await stream.WriteAsync(headerBytes.AsMemory());
        await stream.WriteAsync(bodyBytes.AsMemory());
        await stream.FlushAsync();
    }

    private static async Task SendStatusJsonAsync(Stream stream, bool isAlive)
    {
        string theme = ThemeManager.CurrentTheme == AppTheme.Light ? "light" : "dark";
        string accent = ThemeManager.AccentHex;
        var json = $"{{\"online\": {(isAlive ? "true" : "false")}, \"theme\": \"{theme}\", \"accent\": \"{accent}\"}}";
        var bodyBytes = Encoding.UTF8.GetBytes(json);
        var header = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);

        await stream.WriteAsync(headerBytes.AsMemory());
        await stream.WriteAsync(bodyBytes.AsMemory());
        await stream.FlushAsync();
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    [GeneratedRegex(@"(?im)^User-Agent:\s*([^\r\n]+)", RegexOptions.None, "en-US")]
    private static partial Regex MyRegex();
}
