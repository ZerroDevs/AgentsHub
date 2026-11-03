using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AgentsHub.Services.LlamaCpp;

/// <summary>What this machine can hold: VRAM when a driver can report it, and physical RAM.</summary>
public sealed record MemoryBudget(long VramMb, long RamMb)
{
    /// <summary>
    /// A rough fit for one model file. Weights are not the whole story — context and the runtime
    /// take their own share — so the estimate pads the file size instead of pretending to be exact.
    /// </summary>
    public (string Where, long NeedMb, long HaveMb) Fit(long fileBytes) => Fit(fileBytes, 0, 0);

    /// <summary>
    /// The same verdict with the KV cache counted.
    ///
    /// At the context this app defaults to, the cache can outgrow the weights: a 2B model at 262k
    /// context costs more in cache than in parameters, and a badge that ignores that promises a load
    /// the card cannot finish.
    /// </summary>
    public (string Where, long NeedMb, long HaveMb) Fit(long fileBytes, int contextSize, double kvBytesPerToken, bool gpuUsable = true)
    {
        long needMb = NeedMb(fileBytes, contextSize, kvBytesPerToken);

        // A CPU-only llama.cpp cannot use the card at all, so "fits GPU" is not a promise it can keep.
        if (gpuUsable && VramMb > 0 && needMb <= (long)(VramMb * 0.92))
        {
            return ("gpu", needMb, VramMb);
        }

        if (RamMb > 0 && needMb <= (long)(RamMb * 0.85))
        {
            return ("ram", needMb, RamMb);
        }

        return ("slow", needMb, Math.Max(VramMb, RamMb));
    }

    /// <summary>Weights at their padded estimate plus the cache one context costs, in megabytes.</summary>
    public static long NeedMb(long fileBytes, int contextSize, double kvBytesPerToken)
    {
        long weights = (long)(fileBytes / 1048576.0 * 1.15);
        long cache = kvBytesPerToken > 0 && contextSize > 0
            ? (long)(kvBytesPerToken * contextSize / 1048576.0)
            : 0;

        return weights + cache;
    }

    /// <summary>
    /// The largest context that still fits, chosen from the sizes llama.cpp is usually launched with.
    /// Zero means the weights alone already fail, so there is nothing to suggest.
    /// </summary>
    public int SuggestContext(long fileBytes, double kvBytesPerToken, int cap, bool gpuUsable = true)
    {
        if (kvBytesPerToken <= 0) return 0;

        int[] steps = [4096, 8192, 16384, 32768, 49152, 65536, 131072, 262144];
        int best = 0;

        foreach (int step in steps)
        {
            if (cap > 0 && step > cap) break;

            long need = NeedMb(fileBytes, step, kvBytesPerToken);
            bool fits = (gpuUsable && VramMb > 0 && need <= (long)(VramMb * 0.92))
                || (RamMb > 0 && need <= (long)(RamMb * 0.85));

            if (!fits) break;
            best = step;
        }

        return best;
    }
}

/// <summary>
/// Reads the machine's memory once every ten minutes. Every probe spawns a process, so this is not
/// something to call per row; the Local Models list asks once and reuses the answer.
/// </summary>
public static class LocalHardware
{
    private static readonly object Gate = new();
    private static MemoryBudget _cached = new(0, 0);
    private static DateTime _cachedAt = DateTime.MinValue;

    public static MemoryBudget Budget
    {
        get
        {
            lock (Gate)
            {
                if (DateTime.UtcNow - _cachedAt < TimeSpan.FromMinutes(10)) return _cached;

                _cached = new MemoryBudget(ProbeVram(), ProbeRam());
                _cachedAt = DateTime.UtcNow;
                return _cached;
            }
        }
    }

    /// <summary>
    /// Discreet GPU memory, largest card first. nvidia-smi is exact; the WMI fallback overflows at
    /// 4 GB, so it is used only when nothing better answered.
    /// </summary>
    private static long ProbeVram()
    {
        long nvidia = ProbeFast("nvidia-smi", "--query-gpu=memory.total --format=csv,noheader,nounits");
        if (nvidia > 0) return nvidia;

        string wmi = Run("powershell", "-NoProfile -Command \"(Get-CimInstance Win32_VideoController | Measure-Object -Property AdapterRAM -Maximum).Maximum\"");
        return long.TryParse(new string([.. wmi.Where(char.IsDigit)]), out var value) && value > 0
            ? value / 1048576
            : 0;
    }

    private static long ProbeRam()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                if (GlobalMemoryStatusEx(ref status)) return (long)(status.TotalPhys / 1048576);
            }

            if (OperatingSystem.IsLinux())
            {
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (!line.StartsWith("MemTotal:", StringComparison.Ordinal)) continue;

                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 1 && long.TryParse(parts[1], out var kb)) return kb / 1024;
                }
            }

            if (OperatingSystem.IsMacOS())
            {
                var bytes = Run("sysctl", "-n hw.memsize");
                if (long.TryParse(bytes.Trim(), out var total)) return total / 1048576;
            }
        }
        catch
        {
            // A missing reading is better than a wrong one; callers treat 0 as unknown.
        }

        return 0;
    }

    /// <summary>Largest value from a one-line-per-device query, e.g. two cards report two lines.</summary>
    private static long ProbeFast(string file, string arguments)
    {
        try
        {
            var output = Run(file, arguments);
            long best = 0;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var digits = new string([.. line.Split(',').FirstOrDefault()!.Where(char.IsDigit)]);
                if (long.TryParse(digits, out var value) && value > best) best = value;
            }

            return best;
        }
        catch
        {
            return 0;
        }
    }

    private static string Run(string file, string arguments)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = file,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(info);
            if (process == null) return string.Empty;

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(4000);
            return output;
        }
        catch
        {
            return string.Empty;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
