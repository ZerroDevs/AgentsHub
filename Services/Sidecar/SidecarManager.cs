#nullable enable
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace AgentsHub.Services.Sidecar;

public class SidecarManager : IDisposable
{
    private Process? _process;
    private int _port = 0;
    private string _authToken = string.Empty;
    private readonly Lock _lock = new();

    public int Port => _port;
    public string AuthToken => _authToken;
    public bool IsRunning => _process != null && !_process.HasExited && _port > 0;
    public int ProcessId => _process?.Id ?? 0;
    public string WebSocketUrl => $"ws://127.0.0.1:{_port}/ws?token={_authToken}";
    public string HttpHealthUrl => $"http://127.0.0.1:{_port}/health";

    public event Action<string>? OnLogReceived;
    public event Action? OnExited;

    /// <summary>
    /// Resolves the sidecar binary path for the active platform and processor architecture
    /// </summary>
    public static string ResolveBinaryPath(string? customBasePath = null)
    {
        string baseDir = customBasePath ?? AppContext.BaseDirectory;
        string rootDir = Directory.GetCurrentDirectory();

        string relativeSubPath;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            relativeSubPath = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? Path.Combine("bin", "win-arm64", "AgentsEngine.exe")
                : Path.Combine("bin", "win-x64", "AgentsEngine.exe");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            relativeSubPath = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? Path.Combine("bin", "osx-arm64", "AgentsEngine")
                : Path.Combine("bin", "osx-x64", "AgentsEngine");
        }
        else // Linux & others
        {
            relativeSubPath = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? Path.Combine("bin", "linux-arm64", "AgentsEngine")
                : Path.Combine("bin", "linux-x64", "AgentsEngine");
        }

        // Search candidates: current working dir, base dir, parent directories
        string[] candidates = new[]
        {
            Path.Combine(rootDir, relativeSubPath),
            Path.Combine(baseDir, relativeSubPath),
            Path.Combine(baseDir, Path.GetFileName(relativeSubPath)),
            Path.Combine(rootDir, "dist", relativeSubPath)
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return Path.GetFullPath(path);
            }
        }

        // Return standard expected path even if missing for helpful error messaging
        return Path.GetFullPath(candidates[0]);
    }

    /// <summary>
    /// Checks if a loopback port is available for binding using a socket listener check.
    /// </summary>
    public static bool IsPortAvailable(int port)
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            tcp.Start();
            tcp.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Verifies if the preferred default port is available. If occupied by another program or zombie process,
    /// dynamically scans for the next free loopback port or binds port 0 to let OS assign an unused ephemeral port.
    /// </summary>
    public static int GetAvailablePort(int preferredPort = 48921)
    {
        if (IsPortAvailable(preferredPort))
        {
            return preferredPort;
        }

        // Scan next 100 ports sequentially
        for (int p = preferredPort + 1; p <= preferredPort + 100; p++)
        {
            if (IsPortAvailable(p))
            {
                return p;
            }
        }

        // Ephemeral port assignment fallback
        try
        {
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Tcp);
            socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            return ((System.Net.IPEndPoint)socket.LocalEndPoint!).Port;
        }
        catch
        {
            return 0; // Handshake stdout discovery
        }
    }

    /// <summary>
    /// Starts the sidecar process and waits asynchronously until the readiness handshake is received
    /// </summary>
    public async Task<int> StartAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (IsRunning) return _port;
        }

        string binaryPath = ResolveBinaryPath();
        if (!File.Exists(binaryPath))
        {
            throw new FileNotFoundException(
                $"Sidecar engine binary not found at '{binaryPath}'. Run 'build-sidecar.cmd' or 'bun run build.ts' to compile the runtime.",
                binaryPath);
        }

        SidecarProcessWatchdog.EnsureExecutablePermission(binaryPath);

        // Intelligently resolve free port before spawning process
        int targetPort = GetAvailablePort(48921);
        _port = targetPort;

        // Generate cryptographic token
        byte[] tokenBytes = new byte[32];
        RandomNumberGenerator.Fill(tokenBytes);
        _authToken = Convert.ToHexString(tokenBytes).ToLowerInvariant();

        var tcsReady = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var startInfo = new ProcessStartInfo
        {
            FileName = binaryPath,
            Arguments = $"--port {targetPort} --token {_authToken} --headless",
            WorkingDirectory = Path.GetDirectoryName(binaryPath) ?? AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            // Deliberately NOT CreateNoWindow: that gives the sidecar no console at all, so every
            // console child it spawns (`agy`, `git`) allocates a brand-new console window and flashes
            // on screen. A hidden console is inherited by those children instead, which is the only
            // way to keep them silent — `windowsHide` on the Bun side is not sufficient on its own.
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        startInfo.Environment["AGENTS_SIDECAR_PORT"] = targetPort.ToString();
        startInfo.Environment["AGENTS_SIDECAR_TOKEN"] = _authToken;
        // The sidecar's cwd is the binary's own folder, so it cannot find the profile store by
        // guessing relative paths — it used to invent bin\win-x64\bin\Debug\...\Profiles and run
        // `agy` against an empty home, while the host wrote settings to the real one.
        Helpers.AppPaths.MigrateLegacyAntigravityProfiles();
        startInfo.Environment["AGENTS_PROFILES_DIR"] = Helpers.AppPaths.AntigravityProfilesDir;

        ProcessSpawnLog.Record(startInfo.FileName, startInfo.Arguments, startInfo);
        var proc = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        proc.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;

            OnLogReceived?.Invoke(e.Data);

            // Handshake matching: [AGENTS_ENGINE_READY] port=XXXX pid=YYYY token=ZZZZ
            if (!tcsReady.Task.IsCompleted && e.Data.Contains("[AGENTS_ENGINE_READY]"))
            {
                var match = Regex.Match(e.Data, @"port=(\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int readyPort))
                {
                    _port = readyPort;
                    tcsReady.TrySetResult(readyPort);
                }
            }
        };

        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                OnLogReceived?.Invoke($"[Sidecar Error] {e.Data}");
            }
        };

        proc.Exited += (_, _) =>
        {
            _port = 0;
            tcsReady.TrySetException(new InvalidOperationException($"Sidecar process exited unexpectedly with code {proc.ExitCode}"));
            OnExited?.Invoke();
        };

        if (!proc.Start())
        {
            throw new InvalidOperationException($"Failed to launch sidecar process: '{binaryPath}'");
        }

        _process = proc;
        SidecarProcessWatchdog.AttachProcess(proc);

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        // Wait for handshake with timeout
        using var registration = ct.Register(() => tcsReady.TrySetCanceled());
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await using (linkedCts.Token.Register(() => tcsReady.TrySetException(new TimeoutException("Timed out waiting for sidecar engine readiness handshake."))))
            {
                return await tcsReady.Task;
            }
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_process != null)
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        // Signal stdin EOF to initiate clean shutdown handler
                        _process.StandardInput.Close();
                        if (!_process.WaitForExit(1500))
                        {
                            _process.Kill(true);
                        }
                    }
                }
                catch {}
                finally
                {
                    _process.Dispose();
                    _process = null;
                    _port = 0;
                }
            }
        }
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
