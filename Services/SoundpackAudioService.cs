#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
#if WINDOWS
using System.Media;
#endif
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace AgentsHub.Services;

public sealed class SoundpackAudioConfig
{
    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; } = false; // Default as Turned Off

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "modern";

    [JsonPropertyName("volume")]
    public int Volume { get; set; } = 70;

    [JsonPropertyName("notifyTaskDone")]
    public bool NotifyTaskDone { get; set; } = true;

    [JsonPropertyName("notifyApproval")]
    public bool NotifyApproval { get; set; } = true;

    [JsonPropertyName("notifyCheckpoint")]
    public bool NotifyCheckpoint { get; set; } = true;

    [JsonPropertyName("notifyError")]
    public bool NotifyError { get; set; } = true;
}

public sealed class SoundpackAudioService
{
    public static SoundpackAudioService Instance { get; } = new();

    private readonly string _configPath;
    private SoundpackAudioConfig _config = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public SoundpackAudioService()
    {
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentsHub");
        Directory.CreateDirectory(appData);
        _configPath = Path.Combine(appData, "soundpack.json");

        LoadConfig();
    }

    public SoundpackAudioConfig GetConfig() => _config;

    public void SaveConfig(SoundpackAudioConfig cfg)
    {
        _config = cfg;
        try
        {
            File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, JsonOpts));
        }
        catch { }
    }

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                var loaded = JsonSerializer.Deserialize<SoundpackAudioConfig>(json, JsonOpts);
                if (loaded != null) _config = loaded;
            }
        }
        catch { }
    }

    public void PlaySoundEvent(string eventName, string? theme = null, int? volume = null, bool force = false)
    {
        // 1. Master toggle check: Default is turned off
        if (!force && !_config.IsEnabled) return;

        // 2. Event-specific filter checks
        if (!force)
        {
            bool allowed = eventName switch
            {
                "task_done" => _config.NotifyTaskDone,
                "approval_needed" => _config.NotifyApproval,
                "checkpoint_created" => _config.NotifyCheckpoint,
                "task_failed" => _config.NotifyError,
                _ => true
            };
            if (!allowed) return;
        }

        int vol = volume ?? _config.Volume;
        if (vol <= 0) return;

        var currentTheme = !string.IsNullOrWhiteSpace(theme) ? theme : _config.Theme;

        Task.Run(() =>
        {
            try
            {
                if (string.Equals(currentTheme, "vocal", StringComparison.OrdinalIgnoreCase))
                {
                    PlayVocalCue(eventName);
                    return;
                }

                // Synthesize tone buffer dynamically
                byte[] wavData = SynthesizeTone(eventName, currentTheme, vol);
#if WINDOWS
                if (wavData.Length > 0)
                {
                    using var ms = new MemoryStream(wavData);
                    using var sp = new SoundPlayer(ms);
                    sp.PlaySync();
                }
#endif
            }
            catch { }
        });
    }

    private static void PlayVocalCue(string eventName)
    {
        // Speech synthesis here goes through Windows SAPI, which only exists on Windows. The other
        // themes are synthesized in-process, so they work everywhere.
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            string speech = eventName switch
            {
                "task_done" => "Agent task completed.",
                "approval_needed" => "Action requires approval.",
                "task_failed" => "Agent task failed.",
                "checkpoint_created" => "Checkpoint saved.",
                _ => "Notice."
            };

            // Use Windows PowerShell SAPI synth for 100% offline speech
            var psi = new System.Diagnostics.ProcessStartInfo("powershell", $"-NoProfile -Command \"Add-Type –AssemblyName System.Speech; (New-Object System.Speech.Synthesis.SpeechSynthesizer).Speak('{speech}')\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(3000);
        }
        catch { }
    }

    private static byte[] SynthesizeTone(string eventName, string theme, int volumePercent)
    {
        int sampleRate = 44100;
        double volume = Math.Clamp(volumePercent / 100.0, 0.05, 1.0);

        List<(double freq, double duration)> tones = theme.ToLowerInvariant() switch
        {
            "scifi" => eventName switch
            {
                "task_done" => [(587.33, 0.08), (880.0, 0.08), (1174.66, 0.16)],
                "approval_needed" => [(440.0, 0.1), (659.25, 0.15)],
                "task_failed" => [(329.63, 0.12), (246.94, 0.18)],
                "checkpoint_created" => [(523.25, 0.08), (1046.5, 0.12)],
                _ => [(440.0, 0.1)]
            },
            "arcade" => eventName switch
            {
                "task_done" => [(440.0, 0.05), (554.37, 0.05), (659.25, 0.05), (880.0, 0.12)],
                "approval_needed" => [(349.23, 0.07), (440.0, 0.07)],
                "task_failed" => [(220.0, 0.09), (164.81, 0.14)],
                "checkpoint_created" => [(261.63, 0.05), (329.63, 0.05), (392.0, 0.05), (523.25, 0.1)],
                _ => [(300.0, 0.08)]
            },
            _ => eventName switch // "modern" default
            {
                "task_done" => [(523.25, 0.1), (659.25, 0.1), (783.99, 0.2)],
                "approval_needed" => [(659.25, 0.12), (659.25, 0.12)],
                "task_failed" => [(392.0, 0.12), (293.66, 0.22)],
                "checkpoint_created" => [(440.0, 0.08), (880.0, 0.14)],
                _ => [(523.25, 0.12)]
            }
        };

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        int totalSamples = 0;
        foreach (var (_, d) in tones)
        {
            totalSamples += (int)(sampleRate * d);
        }

        int subChunk2Size = totalSamples * 2;
        int chunkSize = 36 + subChunk2Size;

        // RIFF header
        bw.Write("RIFF"u8);
        bw.Write(chunkSize);
        bw.Write("WAVE"u8);

        // fmt chunk
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write((short)1); // Mono
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);

        // data chunk
        bw.Write("data"u8);
        bw.Write(subChunk2Size);

        foreach (var (freq, duration) in tones)
        {
            int toneSamples = (int)(sampleRate * duration);
            for (int i = 0; i < toneSamples; i++)
            {
                double t = (double)i / sampleRate;
                double fade = 1.0;
                int fadeOutStart = toneSamples - (int)(sampleRate * 0.02);
                if (i > fadeOutStart && fadeOutStart > 0)
                {
                    fade = (double)(toneSamples - i) / (toneSamples - fadeOutStart);
                }

                double sampleVal = Math.Sin(2.0 * Math.PI * freq * t);
                short sampleShort = (short)(sampleVal * volume * fade * 32767);
                bw.Write(sampleShort);
            }
        }

        return ms.ToArray();
    }
}
