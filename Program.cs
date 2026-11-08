using System.Diagnostics;

namespace AgentsHub;

internal static partial class Program
{
    private const string MutexName = @"Global\AgentsHub_SingleInstance_Mutex";
    private const string ShowWindowEventName = @"Global\AgentsHub_ShowWindow_Event";

    [STAThread]
    private static void Main(string[] args)
    {
#if WINDOWS
        using var mutex = new Mutex(true, MutexName, out var isOnlyInstance);

        if (!isOnlyInstance)
        {
            AttachConsole(-1);
            Console.WriteLine("[AgentsHub] Agents Hub is already running. Brought the active window to the foreground.");
            // Wake the running instance instead of exiting silently
            SignalRunningInstance();
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // The Settings toggle was never read: only the command line could start minimised, so the
        // preference looked saved and did nothing. Either source is enough to honour it.
        var startMinimized = args.Any(arg =>
            string.Equals(arg, "--tray", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "-minimized", StringComparison.OrdinalIgnoreCase))
            || Helpers.StartupHelper.GetPreference("StartMinimized", false);

        var form = new MainForm(startMinimized);

        using var showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        ThreadPool.RegisterWaitForSingleObject(
            showWindowEvent,
            (_, _) =>
            {
                if (form.IsHandleCreated)
                {
                    form.BeginInvoke(form.ShowAndActivate);
                }
            },
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        Application.Run(form);
#else
        RunUnixHost(args);
#endif
    }

#if !WINDOWS
    private static void RunUnixHost(string[] args)
    {
        Console.WriteLine("==========================================================");
        Console.WriteLine($" Agents Hub - Desktop Control Plane for AI Coding Agents");
        Console.WriteLine($" Platform: {Services.Platform.PlatformHelper.PlatformName} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");
        Console.WriteLine("==========================================================");

        var hub = new Services.HubProcessService();
        var cliManager = new Services.CliManagerService(hub);
        var lanProxy = new Services.LanProxyService();
        var openCode = new Services.OpenCodeWatcherService();
        using var bridge = new Services.DashboardWebBridge(null, cliManager, lanProxy, openCode);

        // Start Bun Sidecar Engine in background
        _ = Services.Sidecar.SidecarHostService.Instance.StartAsync();

        // Start Linux HTTP + WebSocket bridge server for React SPA and Electron desktop
        using var desktopServer = new Services.Platform.LinuxDesktopServer(bridge, 9125);
        desktopServer.Start();
        Console.WriteLine($"[AgentsHub] Desktop Server listening at: http://127.0.0.1:{desktopServer.Port}");

        int lanPort = lanProxy.ListenPort > 0 ? lanProxy.ListenPort : 9191;
        lanProxy.Start();

        bool headless = args.Any(a => a.Equals("--headless", StringComparison.OrdinalIgnoreCase) ||
                                      a.Equals("-s", StringComparison.OrdinalIgnoreCase) ||
                                      a.Equals("--server", StringComparison.OrdinalIgnoreCase));
        bool noDesktop = headless || args.Any(a => a.Equals("--no-desktop", StringComparison.OrdinalIgnoreCase));

        using var exitEvent = new ManualResetEventSlim(false);
        Process? electronProcess = null;

        if (!noDesktop)
        {
            electronProcess = LaunchElectronDesktop();
            if (electronProcess != null)
            {
                Console.WriteLine("[AgentsHub] Launched native desktop window.");
                electronProcess.EnableRaisingEvents = true;
                electronProcess.Exited += (_, _) => exitEvent.Set();
            }
            else
            {
                Console.WriteLine($"[AgentsHub] Launching dashboard in browser: http://127.0.0.1:{desktopServer.Port}");
                Services.Platform.PlatformHelper.OpenUrlInBrowser($"http://127.0.0.1:{desktopServer.Port}");
            }
        }
        else
        {
            Console.WriteLine($"[AgentsHub] Running in server/headless mode. Dashboard at: http://127.0.0.1:{desktopServer.Port}");
        }

        Console.WriteLine("[AgentsHub] Agents Hub is running. Press Ctrl+C or close the window to terminate.");
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\r\n[AgentsHub] Shutting down services...");
            try
            {
                if (electronProcess != null && !electronProcess.HasExited)
                {
                    electronProcess.Kill(entireProcessTree: true);
                }
            }
            catch { }
            exitEvent.Set();
        };

        exitEvent.Wait();
        lanProxy.Dispose();
        cliManager.Dispose();
        Console.WriteLine("[AgentsHub] Stopped cleanly.");
    }

    private static Process? LaunchElectronDesktop()
    {
        try
        {
            string cwd = Directory.GetCurrentDirectory();
            string electronBin = Path.Combine(cwd, "node_modules", ".bin", "electron");
            bool hasLocalElectron = File.Exists(electronBin);

            string mainScript = Path.Combine(cwd, "electron", "main.cjs");
            if (!File.Exists(mainScript))
            {
                mainScript = Path.Combine(AppContext.BaseDirectory, "electron", "main.cjs");
            }
            if (!File.Exists(mainScript)) return null;

            string electronArgs = hasLocalElectron
                ? $"\"{mainScript}\" --no-sandbox"
                : $"electron \"{mainScript}\" --no-sandbox";

            var psi = new ProcessStartInfo
            {
                FileName = hasLocalElectron ? electronBin : "npx",
                Arguments = electronArgs,
                WorkingDirectory = cwd,
                UseShellExecute = false,
            };
            psi.Environment["AGENTSHUB_URL"] = "http://127.0.0.1:9125/index.html";

            string? display = Environment.GetEnvironmentVariable("DISPLAY");
            if (!string.IsNullOrEmpty(display))
            {
                psi.Environment["DISPLAY"] = display;
            }

            return Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AgentsHub] Could not launch Electron desktop: {ex.Message}");
            return null;
        }
    }
#endif

#if WINDOWS
    private static void SignalRunningInstance()
    {
        try
        {
            using var showWindowEvent = EventWaitHandle.OpenExisting(ShowWindowEventName);
            showWindowEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // First instance has not created the event yet; exiting is still correct
        }
        catch (UnauthorizedAccessException)
        {
            // Cross-session access denied; exiting is still correct
        }
    }

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool AttachConsole(int dwProcessId);
#endif
}
