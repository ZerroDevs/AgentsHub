using System.Text.Json;
using AgentsHub.Services;

namespace AgentsHub.Services;

/// <summary>
/// The composer's upload channel: begin, slices, finish.
///
/// Each slice is a small web message, so a large PDF no longer travels as one enormous JSON string
/// through the bridge. What the engines receive at the end is a plain path — the same thing a CLI
/// opens and a local model reads.
/// </summary>
public sealed partial class DashboardWebBridge
{
    private Task HandleAttachmentBeginAsync(JsonElement data, string? requestId)
    {
        string name = data.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "attachment" : "attachment";

        // Anything an earlier crash left behind is not going to be finished now.
        AttachmentStore.Sweep(TimeSpan.FromHours(6));

        var (uploadId, _) = AttachmentStore.Begin(name);
        SendResponse(requestId, true, new { uploadId });

        return Task.CompletedTask;
    }

    private Task HandleAttachmentChunkAsync(JsonElement data, string? requestId)
    {
        string uploadId = data.TryGetProperty("uploadId", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
        string encoded = data.TryGetProperty("data", out var dataEl) ? dataEl.GetString() ?? string.Empty : string.Empty;

        if (uploadId.Length == 0 || encoded.Length == 0)
        {
            SendResponse(requestId, false, "A slice needs an upload id and its bytes.");
            return Task.CompletedTask;
        }

        byte[] bytes;

        try
        {
            bytes = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            SendResponse(requestId, false, "That attachment slice was not valid base64.");
            return Task.CompletedTask;
        }

        if (!AttachmentStore.Append(uploadId, bytes))
        {
            SendResponse(requestId, false, "The upload was refused: unknown id, or the file is past the size cap.");
            return Task.CompletedTask;
        }

        SendResponse(requestId, true, new { received = bytes.Length });
        return Task.CompletedTask;
    }

    private Task HandleAttachmentEndAsync(JsonElement data, string? requestId)
    {
        string uploadId = data.TryGetProperty("uploadId", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;

        string? path = AttachmentStore.Complete(uploadId);
        if (path is null)
        {
            SendResponse(requestId, false, "The upload finished without a file.");
            return Task.CompletedTask;
        }

        long size = 0;
        try { size = new FileInfo(path).Length; } catch (IOException) { }

        SendResponse(requestId, true, new { path, size });
        return Task.CompletedTask;
    }

    private Task HandleAttachmentAbortAsync(JsonElement data, string? requestId)
    {
        string uploadId = data.TryGetProperty("uploadId", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;

        AttachmentStore.Abort(uploadId);
        SendResponse(requestId, true, new { aborted = true });

        return Task.CompletedTask;
    }
}
