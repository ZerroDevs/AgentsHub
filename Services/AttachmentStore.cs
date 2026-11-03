using System.Collections.Concurrent;

namespace AgentsHub.Services;

/// <summary>
/// Where the composer's attachments become files on this side.
///
/// A browser message carries text, so an upload had to be base64 inside JSON: one 60 MB PDF became an
/// 80 MB string in a single web message, copied again into a byte array. Writing it in slices keeps
/// each message small and leaves what every engine actually wants — a real path on disk.
///
/// Files live in the temp folder under a recognisable prefix so <see cref="Sweep"/> can find the ones
/// a crash or a cancelled send left behind.
/// </summary>
public static class AttachmentStore
{
    public const string Prefix = "agentshub-attach-";

    /// <summary>A cap that no sane attachment reaches; it stops a runaway upload filling the disk.</summary>
    private const long MaxBytes = 256L * 1024 * 1024;

    private sealed class Upload
    {
        public required string Path { get; init; }
        public long Received { get; set; }
    }

    private static readonly ConcurrentDictionary<string, Upload> Pending = new(StringComparer.Ordinal);

    public static (string UploadId, string Path) Begin(string? name)
    {
        string safe = string.Concat((name ?? "attachment").Split(Path.GetInvalidFileNameChars())).Trim();
        if (safe.Length == 0) safe = "attachment";

        string id = Guid.NewGuid().ToString("N");
        string path = Path.Combine(Path.GetTempPath(), $"{Prefix}{id}-{safe}");

        Pending[id] = new Upload { Path = path };

        return (id, path);
    }

    /// <summary>Appends one slice. False means the id is unknown or the file would grow past the cap.</summary>
    public static bool Append(string uploadId, ReadOnlySpan<byte> bytes)
    {
        if (!Pending.TryGetValue(uploadId, out var upload)) return false;
        if (upload.Received + bytes.Length > MaxBytes) return false;

        try
        {
            using var stream = new FileStream(upload.Path, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(bytes);

            upload.Received += bytes.Length;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The finished file's path, or null when the id is unknown or nothing was written.</summary>
    public static string? Complete(string uploadId)
    {
        if (!Pending.TryRemove(uploadId, out var upload)) return null;
        if (!File.Exists(upload.Path)) return null;

        return upload.Path;
    }

    public static void Abort(string uploadId)
    {
        if (!Pending.TryRemove(uploadId, out var upload)) return;

        try
        {
            if (File.Exists(upload.Path)) File.Delete(upload.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The sweep will get it.
        }
    }

    /// <summary>Writes a complete file at once, for a caller that already holds the bytes.</summary>
    public static string Write(string? name, byte[] bytes)
    {
        var (_, path) = Begin(name);

        try
        {
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    /// <summary>
    /// Deletes attachment files older than the given age. Six hours covers a long agent turn; a day
    /// at startup covers what a crash or a closed window left behind.
    /// </summary>
    public static int Sweep(TimeSpan minimumAge)
    {
        int removed = 0;

        try
        {
            string folder = Path.GetTempPath();
            DateTime cutoff = DateTime.UtcNow - minimumAge;

            foreach (string file in Directory.EnumerateFiles(folder, Prefix + "*"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) >= cutoff) continue;

                    File.Delete(file);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A file another process still holds is left for the next sweep.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable temp folder is not worth failing a turn over.
        }

        return removed;
    }
}
