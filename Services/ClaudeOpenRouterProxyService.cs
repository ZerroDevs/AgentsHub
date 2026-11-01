#nullable enable
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentsHub.Services;

/// <summary>
/// Embedded local HTTP translation proxy that exposes the Anthropic Messages API
/// (POST /v1/messages) on http://127.0.0.1:9230/ and translates requests to OpenAI
/// Chat Completions, forwarding them to OpenRouter with streaming SSE and tool calling.
/// </summary>
public sealed class ClaudeOpenRouterProxyService : IDisposable
{
    private static readonly Lazy<ClaudeOpenRouterProxyService> _lazyInstance =
        new(() => new ClaudeOpenRouterProxyService());
    public static ClaudeOpenRouterProxyService Instance => _lazyInstance.Value;

    public const int DefaultPort = 9230;

    private readonly Lock _lock = new();
    private readonly Lock _keyLock = new();
    private int _activeKeyIndex = 0;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private readonly HttpClient _httpClient;

    public int Port { get; private set; } = DefaultPort;
    public bool IsRunning { get; private set; }

    public event Action<string>? LogMessage;

    private ClaudeOpenRouterProxyService()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
    }

    public bool Start(int port = DefaultPort)
    {
        lock (_lock)
        {
            if (IsRunning && Port == port)
            {
                return true;
            }

            if (IsRunning)
            {
                Stop();
            }

            Port = port;
            _cts = new CancellationTokenSource();

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();

                IsRunning = true;
                _listenTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
                LogMessage?.Invoke($"Claude OpenRouter Translation Proxy active on http://127.0.0.1:{port}/");
                return true;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"Failed to start Claude Proxy on port {port}: {ex.Message}");
                Stop();
                return false;
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!IsRunning && _listener == null) return;

            IsRunning = false;
            try { _cts?.Cancel(); } catch { }

            try
            {
                if (_listener != null && _listener.IsListening)
                {
                    _listener.Stop();
                    _listener.Close();
                }
            }
            catch { }

            _listener = null;
            _cts?.Dispose();
            _cts = null;
            LogMessage?.Invoke("Claude OpenRouter Translation Proxy stopped.");
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var listener = _listener;
                if (listener == null || !listener.IsListening) break;

                var context = await listener.GetContextAsync().ConfigureAwait(false);
                _ = Task.Run(() => ProcessContextAsync(context, ct), ct);
            }
            catch (HttpListenerException)
            {
                // Expected when listener stops
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
                    Debug.WriteLine($"[ClaudeProxy] AcceptLoop error: {ex.Message}");
                }
            }
        }
    }

    private async Task ProcessContextAsync(HttpListenerContext context, CancellationToken ct)
    {
        var req = context.Request;
        var resp = context.Response;

        // Add standard CORS headers
        resp.Headers.Set("Cache-Control", "no-store");
        resp.Headers.Set("X-Content-Type-Options", "nosniff");
        resp.Headers.Set("Access-Control-Allow-Methods", "POST, GET, OPTIONS");
        resp.Headers.Set("Access-Control-Allow-Headers", "*");

        if (string.Equals(req.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            resp.StatusCode = (int)HttpStatusCode.OK;
            resp.Close();
            return;
        }

        var path = req.Url?.AbsolutePath.TrimEnd('/') ?? "";

        // Health check endpoint
        if (string.Equals(req.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(path) || path == "/health" || path == "/v1")
            {
                resp.ContentType = "application/json";
                resp.StatusCode = 200;
                var healthJson = Encoding.UTF8.GetBytes("{\"status\":\"ok\",\"proxy\":\"ClaudeOpenRouterTranslationProxy\",\"port\":" + Port + "}");
                await resp.OutputStream.WriteAsync(healthJson, ct).ConfigureAwait(false);
                resp.Close();
                return;
            }
        }

        // Handle Messages Endpoint (POST /v1/messages or POST /messages)
        if (string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase) &&
            (path.EndsWith("/messages", StringComparison.OrdinalIgnoreCase)))
        {
            await HandleMessagesRequestAsync(context, ct).ConfigureAwait(false);
            return;
        }

        // Return Anthropic-compatible 404 for unknown routes
        await SendAnthropicErrorAsync(resp, HttpStatusCode.NotFound, $"Endpoint '{path}' not found on Claude Local Proxy.", ct).ConfigureAwait(false);
    }

    private async Task HandleMessagesRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        var req = context.Request;
        var resp = context.Response;

        try
        {
            string requestBody;
            using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
            {
                requestBody = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(requestBody))
            {
                await SendAnthropicErrorAsync(resp, HttpStatusCode.BadRequest, "Request body is empty.", ct).ConfigureAwait(false);
                return;
            }

            using var anthropicDoc = JsonDocument.Parse(requestBody);
            var root = anthropicDoc.RootElement;

            // Resolve failover pool of API keys
            var activeProv = AiConfigService.Instance.GetActiveClaudeGatewayProvider();
            var keysPool = activeProv.GetEffectiveApiKeys();

            string? headerApiKey = req.Headers["x-api-key"];
            if (string.IsNullOrWhiteSpace(headerApiKey))
            {
                var authHeader = req.Headers["Authorization"] ?? "";
                if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    headerApiKey = authHeader[7..].Trim();
                }
            }

            if (keysPool.Count == 0)
            {
                var fallbackKey = AiConfigService.Instance.GetOpenRouterApiKey();
                if (!string.IsNullOrWhiteSpace(fallbackKey))
                {
                    keysPool.Add(fallbackKey);
                }
            }

            // No caller key means no upstream call. With no configured pool, a caller may bring
            // its own key and spend that key; it is never served by the stored pool by accident.
            if (string.IsNullOrWhiteSpace(headerApiKey))
            {
                await SendAnthropicErrorAsync(resp, HttpStatusCode.Unauthorized, "Unauthorized: the local proxy requires an API key.", ct).ConfigureAwait(false);
                return;
            }

            if (keysPool.Count == 0)
            {
                keysPool.Add(headerApiKey);
            }
            else if (!keysPool.Contains(headerApiKey, StringComparer.Ordinal))
            {
                await SendAnthropicErrorAsync(resp, HttpStatusCode.Unauthorized, "Unauthorized: the local proxy requires the configured API key.", ct).ConfigureAwait(false);
                return;
            }

            // Resolve target model
            string model = root.TryGetProperty("model", out var mEl) && mEl.ValueKind == JsonValueKind.String
                ? mEl.GetString()!
                : AiConfigService.Instance.GetClaudeSelectedModel();

            bool isStream = root.TryGetProperty("stream", out var sEl) && sEl.ValueKind is JsonValueKind.True;

            // Translate Anthropic payload to OpenAI Chat Completions payload
            var openAiPayload = TranslateAnthropicToOpenAi(root, model, isStream);
            var openAiJson = openAiPayload.ToJsonString();

            // Resolve upstream destination: default to OpenRouter
            var upstreamUrl = "https://openrouter.ai/api/v1/chat/completions";

            int totalKeys = keysPool.Count;
            int startIdx;
            lock (_keyLock)
            {
                startIdx = _activeKeyIndex % totalKeys;
            }

            HttpResponseMessage? successfulResp = null;
            string lastErrorMessage = "";
            HttpStatusCode lastStatusCode = HttpStatusCode.InternalServerError;

            for (int attempt = 0; attempt < totalKeys; attempt++)
            {
                int currentIdx = (startIdx + attempt) % totalKeys;
                string currentKey = keysPool[currentIdx];

                using var upstreamReq = new HttpRequestMessage(HttpMethod.Post, upstreamUrl);
                upstreamReq.Headers.Add("Authorization", $"Bearer {currentKey}");
                upstreamReq.Headers.Add("HTTP-Referer", "https://app.agentshub.local");
                upstreamReq.Headers.Add("X-Title", "AgentsHub Claude Code Local Proxy");
                upstreamReq.Content = new StringContent(openAiJson, Encoding.UTF8, "application/json");

                HttpResponseMessage upstreamResp;
                try
                {
                    upstreamResp = await _httpClient.SendAsync(
                        upstreamReq,
                        isStream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
                        ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    lastErrorMessage = $"Upstream connection failed: {ex.Message}";
                    continue;
                }

                if (upstreamResp.IsSuccessStatusCode)
                {
                    successfulResp = upstreamResp;
                    lock (_keyLock)
                    {
                        _activeKeyIndex = currentIdx;
                    }
                    if (attempt > 0)
                    {
                        LogMessage?.Invoke($"[Local Proxy] Successfully rotated to key #{currentIdx + 1} ({MaskKey(currentKey)}).");
                    }
                    break;
                }

                var errContent = await upstreamResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                string cleanMsg = ExtractErrorMessage(errContent) ?? $"Upstream provider returned HTTP {(int)upstreamResp.StatusCode}";
                lastErrorMessage = cleanMsg;
                lastStatusCode = upstreamResp.StatusCode;

                bool isRateLimit = IsRateLimit(upstreamResp.StatusCode, cleanMsg);
                if (isRateLimit && totalKeys > 1 && attempt < totalKeys - 1)
                {
                    int nextIdx = (currentIdx + 1) % totalKeys;
                    LogMessage?.Invoke($"[Local Proxy] Key #{currentIdx + 1} ({MaskKey(currentKey)}) hit rate limit ({cleanMsg}). Auto-switching to key #{nextIdx + 1} ({attempt + 1}/{totalKeys})...");
                    upstreamResp.Dispose();
                    continue;
                }
                else
                {
                    upstreamResp.Dispose();
                    break;
                }
            }

            if (successfulResp == null)
            {
                string msg = totalKeys > 1
                    ? $"All {totalKeys} configured OpenRouter API keys hit rate limits or failed. Last error: {lastErrorMessage}"
                    : lastErrorMessage;
                await SendAnthropicErrorAsync(resp, lastStatusCode, msg, ct).ConfigureAwait(false);
                return;
            }

            using (successfulResp)
            {
                if (isStream)
                {
                    await StreamOpenAiToAnthropicSseAsync(successfulResp, resp, model, ct).ConfigureAwait(false);
                }
                else
                {
                    var fullOpenAiResponse = await successfulResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    var anthropicResponseJson = TranslateOpenAiResponseToAnthropic(fullOpenAiResponse, model);
                    resp.ContentType = "application/json; charset=utf-8";
                    resp.StatusCode = (int)HttpStatusCode.OK;
                    var bytes = Encoding.UTF8.GetBytes(anthropicResponseJson);
                    await resp.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
                    resp.Close();
                }
            }
        }
        catch (OperationCanceledException)
        {
            try { resp.Close(); } catch { }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClaudeProxy] Error processing messages: {ex}");
            try
            {
                await SendAnthropicErrorAsync(resp, HttpStatusCode.InternalServerError, $"Local proxy translation exception: {ex.Message}", ct).ConfigureAwait(false);
            }
            catch { }
        }
    }

    private static JsonObject TranslateAnthropicToOpenAi(JsonElement root, string model, bool stream)
    {
        var openAiObj = new JsonObject
        {
            ["model"] = model,
            ["stream"] = stream
        };

        if (root.TryGetProperty("max_tokens", out var maxTokEl) && maxTokEl.ValueKind == JsonValueKind.Number)
        {
            openAiObj["max_tokens"] = maxTokEl.GetInt32();
        }

        if (root.TryGetProperty("temperature", out var tempEl) && tempEl.ValueKind == JsonValueKind.Number)
        {
            openAiObj["temperature"] = tempEl.GetDouble();
        }

        var openAiMessages = new JsonArray();

        // 1. Anthropic top-level system parameter
        if (root.TryGetProperty("system", out var sysEl))
        {
            string systemPrompt = "";
            if (sysEl.ValueKind == JsonValueKind.String)
            {
                systemPrompt = sysEl.GetString() ?? "";
            }
            else if (sysEl.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var block in sysEl.EnumerateArray())
                {
                    if (block.TryGetProperty("text", out var tEl) && tEl.ValueKind == JsonValueKind.String)
                    {
                        sb.AppendLine(tEl.GetString());
                    }
                }
                systemPrompt = sb.ToString().Trim();
            }

            if (!string.IsNullOrWhiteSpace(systemPrompt))
            {
                openAiMessages.Add(new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = systemPrompt
                });
            }
        }

        // 2. Anthropic messages array
        if (root.TryGetProperty("messages", out var msgsEl) && msgsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in msgsEl.EnumerateArray())
            {
                string role = m.TryGetProperty("role", out var rEl) && rEl.ValueKind == JsonValueKind.String
                    ? rEl.GetString()!
                    : "user";

                if (!m.TryGetProperty("content", out var contentEl)) continue;

                if (contentEl.ValueKind == JsonValueKind.String)
                {
                    openAiMessages.Add(new JsonObject
                    {
                        ["role"] = role,
                        ["content"] = contentEl.GetString() ?? ""
                    });
                    continue;
                }

                if (contentEl.ValueKind == JsonValueKind.Array)
                {
                    var textBuilder = new StringBuilder();
                    var toolCalls = new JsonArray();
                    var toolResults = new List<(string callId, string content)>();

                    foreach (var block in contentEl.EnumerateArray())
                    {
                        string blockType = block.TryGetProperty("type", out var bTypeEl) && bTypeEl.ValueKind == JsonValueKind.String
                            ? bTypeEl.GetString()!
                            : "text";

                        if (blockType == "text" && block.TryGetProperty("text", out var tEl))
                        {
                            textBuilder.Append(tEl.GetString());
                        }
                        else if (blockType == "tool_use")
                        {
                            string id = block.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : Guid.NewGuid().ToString("N");
                            string name = block.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "tool";
                            string inputJson = block.TryGetProperty("input", out var inEl) ? inEl.GetRawText() : "{}";

                            toolCalls.Add(new JsonObject
                            {
                                ["id"] = id,
                                ["type"] = "function",
                                ["function"] = new JsonObject
                                {
                                    ["name"] = name,
                                    ["arguments"] = inputJson
                                }
                            });
                        }
                        else if (blockType == "tool_result")
                        {
                            string toolCallId = block.TryGetProperty("tool_use_id", out var tuEl) ? tuEl.GetString() ?? "" : "";
                            string resultText = "";

                            if (block.TryGetProperty("content", out var resContEl))
                            {
                                if (resContEl.ValueKind == JsonValueKind.String)
                                {
                                    resultText = resContEl.GetString() ?? "";
                                }
                                else if (resContEl.ValueKind == JsonValueKind.Array)
                                {
                                    var rsb = new StringBuilder();
                                    foreach (var cb in resContEl.EnumerateArray())
                                    {
                                        if (cb.TryGetProperty("text", out var rTxtEl))
                                        {
                                            rsb.AppendLine(rTxtEl.GetString());
                                        }
                                    }
                                    resultText = rsb.ToString().Trim();
                                }
                            }

                            toolResults.Add((toolCallId, resultText));
                        }
                    }

                    // For assistant: emit text and/or tool_calls
                    if (role == "assistant")
                    {
                        var asstMsg = new JsonObject { ["role"] = "assistant" };
                        var textStr = textBuilder.ToString();
                        asstMsg["content"] = string.IsNullOrEmpty(textStr) && toolCalls.Count > 0 ? null : textStr;

                        if (toolCalls.Count > 0)
                        {
                            asstMsg["tool_calls"] = toolCalls;
                        }
                        openAiMessages.Add(asstMsg);
                    }
                    else // For user: emit user text message and separate tool result messages
                    {
                        var userText = textBuilder.ToString();
                        if (!string.IsNullOrEmpty(userText) || toolResults.Count == 0)
                        {
                            openAiMessages.Add(new JsonObject
                            {
                                ["role"] = "user",
                                ["content"] = userText
                            });
                        }

                        foreach (var (toolCallId, resContent) in toolResults)
                        {
                            openAiMessages.Add(new JsonObject
                            {
                                ["role"] = "tool",
                                ["tool_call_id"] = toolCallId,
                                ["content"] = resContent
                            });
                        }
                    }
                }
            }
        }

        openAiObj["messages"] = openAiMessages;

        // 3. Anthropic tools definition -> OpenAI functions
        if (root.TryGetProperty("tools", out var toolsEl) && toolsEl.ValueKind == JsonValueKind.Array)
        {
            var openAiTools = new JsonArray();
            foreach (var t in toolsEl.EnumerateArray())
            {
                string name = t.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                string desc = t.TryGetProperty("description", out var dEl) ? dEl.GetString() ?? "" : "";
                JsonNode? paramsNode = null;

                if (t.TryGetProperty("input_schema", out var sEl))
                {
                    paramsNode = JsonNode.Parse(sEl.GetRawText());
                }

                openAiTools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = name,
                        ["description"] = desc,
                        ["parameters"] = paramsNode ?? new JsonObject { ["type"] = "object" }
                    }
                });
            }

            if (openAiTools.Count > 0)
            {
                openAiObj["tools"] = openAiTools;
            }
        }

        return openAiObj;
    }

    private static async Task StreamOpenAiToAnthropicSseAsync(
        HttpResponseMessage upstreamResp,
        HttpListenerResponse clientResp,
        string model,
        CancellationToken ct)
    {
        clientResp.ContentType = "text/event-stream; charset=utf-8";
        clientResp.Headers.Set("Cache-Control", "no-cache");
        clientResp.Headers.Set("Connection", "keep-alive");
        clientResp.StatusCode = 200;

        var output = clientResp.OutputStream;
        var messageId = "msg_" + Guid.NewGuid().ToString("N")[..20];

        // 1. Initial Anthropic event: message_start
        var startPayload = new
        {
            type = "message_start",
            message = new
            {
                id = messageId,
                type = "message",
                role = "assistant",
                content = Array.Empty<object>(),
                model,
                stop_reason = (string?)null,
                stop_sequence = (string?)null,
                usage = new { input_tokens = 0, output_tokens = 0 }
            }
        };
        await WriteSseEventAsync(output, "message_start", JsonSerializer.Serialize(startPayload), ct).ConfigureAwait(false);

        using var stream = await upstreamResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        int currentContentBlockIndex = -1;
        bool isTextBlockActive = false;
        int activeToolBlockIndex = -1;
        var activeToolCalls = new Dictionary<int, string>(); // map openai tool index to anthropic content block index
        int nextBlockIndex = 0;
        int outputTokensCount = 0;
        string stopReason = "end_turn";

        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

            var dataStr = line[5..].Trim();
            if (dataStr == "[DONE]") break;

            try
            {
                using var chunkDoc = JsonDocument.Parse(dataStr);
                var root = chunkDoc.RootElement;

                if (!root.TryGetProperty("choices", out var choicesEl) || choicesEl.GetArrayLength() == 0)
                {
                    continue;
                }

                var choice = choicesEl[0];

                // Finish reason
                if (choice.TryGetProperty("finish_reason", out var finEl) && finEl.ValueKind == JsonValueKind.String)
                {
                    var oaiFinish = finEl.GetString();
                    if (!string.IsNullOrEmpty(oaiFinish))
                    {
                        stopReason = oaiFinish switch
                        {
                            "tool_calls" => "tool_use",
                            "length" => "max_tokens",
                            _ => "end_turn"
                        };
                    }
                }

                if (!choice.TryGetProperty("delta", out var deltaEl)) continue;

                // Handle text content delta
                if (deltaEl.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                {
                    var textChunk = contentEl.GetString();
                    if (!string.IsNullOrEmpty(textChunk))
                    {
                        outputTokensCount++;

                        if (!isTextBlockActive)
                        {
                            currentContentBlockIndex = nextBlockIndex++;
                            isTextBlockActive = true;

                            var blockStart = new
                            {
                                type = "content_block_start",
                                index = currentContentBlockIndex,
                                content_block = new { type = "text", text = "" }
                            };
                            await WriteSseEventAsync(output, "content_block_start", JsonSerializer.Serialize(blockStart), ct).ConfigureAwait(false);
                        }

                        var blockDelta = new
                        {
                            type = "content_block_delta",
                            index = currentContentBlockIndex,
                            delta = new { type = "text_delta", text = textChunk }
                        };
                        await WriteSseEventAsync(output, "content_block_delta", JsonSerializer.Serialize(blockDelta), ct).ConfigureAwait(false);
                    }
                }

                // Handle tool calls delta
                if (deltaEl.TryGetProperty("tool_calls", out var tcArrayEl) && tcArrayEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in tcArrayEl.EnumerateArray())
                    {
                        int tcIdx = tc.TryGetProperty("index", out var idxEl) && idxEl.ValueKind == JsonValueKind.Number ? idxEl.GetInt32() : 0;
                        string? callId = tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;

                        string? funcName = null;
                        string? argsChunk = null;

                        if (tc.TryGetProperty("function", out var fEl))
                        {
                            if (fEl.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String)
                            {
                                funcName = nEl.GetString();
                            }
                            if (fEl.TryGetProperty("arguments", out var aEl) && aEl.ValueKind == JsonValueKind.String)
                            {
                                argsChunk = aEl.GetString();
                            }
                        }

                        // If new tool call started
                        if (!activeToolCalls.TryGetValue(tcIdx, out string? value))
                        {
                            // Close active text block if open
                            if (isTextBlockActive)
                            {
                                var blockStop = new { type = "content_block_stop", index = currentContentBlockIndex };
                                await WriteSseEventAsync(output, "content_block_stop", JsonSerializer.Serialize(blockStop), ct).ConfigureAwait(false);
                                isTextBlockActive = false;
                            }

                            activeToolBlockIndex = nextBlockIndex++;
                            value = activeToolBlockIndex.ToString();
                            activeToolCalls[tcIdx] = value;

                            var toolStart = new
                            {
                                type = "content_block_start",
                                index = activeToolBlockIndex,
                                content_block = new
                                {
                                    type = "tool_use",
                                    id = callId ?? "call_" + Guid.NewGuid().ToString("N")[..16],
                                    name = funcName ?? "tool",
                                    input = new { }
                                }
                            };
                            await WriteSseEventAsync(output, "content_block_start", JsonSerializer.Serialize(toolStart), ct).ConfigureAwait(false);
                        }

                        // Send argument deltas
                        if (!string.IsNullOrEmpty(argsChunk))
                        {
                            outputTokensCount++;
                            int targetBlockIdx = int.Parse(value);
                            var toolDelta = new
                            {
                                type = "content_block_delta",
                                index = targetBlockIdx,
                                delta = new { type = "input_json_delta", partial_json = argsChunk }
                            };
                            await WriteSseEventAsync(output, "content_block_delta", JsonSerializer.Serialize(toolDelta), ct).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClaudeProxy] SSE chunk parse warning: {ex.Message}");
            }
        }

        // Close any lingering open content block
        if (isTextBlockActive)
        {
            var blockStop = new { type = "content_block_stop", index = currentContentBlockIndex };
            await WriteSseEventAsync(output, "content_block_stop", JsonSerializer.Serialize(blockStop), ct).ConfigureAwait(false);
        }
        foreach (var kvp in activeToolCalls)
        {
            var blockStop = new { type = "content_block_stop", index = int.Parse(kvp.Value) };
            await WriteSseEventAsync(output, "content_block_stop", JsonSerializer.Serialize(blockStop), ct).ConfigureAwait(false);
        }

        // Anthropic message_delta & message_stop events
        var msgDelta = new
        {
            type = "message_delta",
            delta = new { stop_reason = stopReason, stop_sequence = (string?)null },
            usage = new { output_tokens = outputTokensCount }
        };
        await WriteSseEventAsync(output, "message_delta", JsonSerializer.Serialize(msgDelta), ct).ConfigureAwait(false);

        var msgStop = new { type = "message_stop" };
        await WriteSseEventAsync(output, "message_stop", JsonSerializer.Serialize(msgStop), ct).ConfigureAwait(false);

        ProxyTelemetryService.Instance.RecordRequest(model, Math.Max(16, outputTokensCount / 3), outputTokensCount, "OpenRouter (Claude Code)");

        await output.FlushAsync(ct).ConfigureAwait(false);
        clientResp.Close();
    }

    private static async Task WriteSseEventAsync(Stream stream, string eventName, string dataJson, CancellationToken ct)
    {
        var eventBytes = Encoding.UTF8.GetBytes($"event: {eventName}\ndata: {dataJson}\n\n");
        await stream.WriteAsync(eventBytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string TranslateOpenAiResponseToAnthropic(string openAiJson, string model)
    {
        using var doc = JsonDocument.Parse(openAiJson);
        var root = doc.RootElement;

        var contentArray = new JsonArray();
        string stopReason = "end_turn";

        if (root.TryGetProperty("choices", out var choicesEl) && choicesEl.GetArrayLength() > 0)
        {
            var firstChoice = choicesEl[0];
            if (firstChoice.TryGetProperty("finish_reason", out var finEl) && finEl.ValueKind == JsonValueKind.String)
            {
                stopReason = finEl.GetString() == "tool_calls" ? "tool_use" : "end_turn";
            }

            if (firstChoice.TryGetProperty("message", out var msgEl))
            {
                if (msgEl.TryGetProperty("content", out var cEl) && cEl.ValueKind == JsonValueKind.String)
                {
                    var text = cEl.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        contentArray.Add(new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = text
                        });
                    }
                }

                if (msgEl.TryGetProperty("tool_calls", out var tcEl) && tcEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in tcEl.EnumerateArray())
                    {
                        string id = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                        string name = "";
                        JsonNode? inputObj = null;

                        if (tc.TryGetProperty("function", out var fEl))
                        {
                            name = fEl.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                            if (fEl.TryGetProperty("arguments", out var aEl) && aEl.ValueKind == JsonValueKind.String)
                            {
                                try { inputObj = JsonNode.Parse(aEl.GetString()!); } catch { }
                            }
                        }

                        contentArray.Add(new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = id,
                            ["name"] = name,
                            ["input"] = inputObj ?? new JsonObject()
                        });
                    }
                }
            }
        }

        int inTokens = 0, outTokens = 0;
        if (root.TryGetProperty("usage", out var usgEl))
        {
            if (usgEl.TryGetProperty("prompt_tokens", out var pEl)) inTokens = pEl.GetInt32();
            if (usgEl.TryGetProperty("completion_tokens", out var cpEl)) outTokens = cpEl.GetInt32();
        }

        ProxyTelemetryService.Instance.RecordRequest(model, inTokens, outTokens, "OpenRouter (Claude Code)");

        var anthropicObj = new JsonObject
        {
            ["id"] = "msg_" + Guid.NewGuid().ToString("N")[..20],
            ["type"] = "message",
            ["role"] = "assistant",
            ["content"] = contentArray,
            ["model"] = model,
            ["stop_reason"] = stopReason,
            ["stop_sequence"] = null,
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = inTokens,
                ["output_tokens"] = outTokens
            }
        };

        return anthropicObj.ToJsonString();
    }

    private static bool IsRateLimit(HttpStatusCode statusCode, string? errorMessage)
    {
        if (statusCode == (HttpStatusCode)429) return true;
        if (statusCode == (HttpStatusCode)402) return true;
        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            var lower = errorMessage.ToLowerInvariant();
            if (lower.Contains("rate limit") ||
                lower.Contains("rate_limit") ||
                lower.Contains("too many requests") ||
                lower.Contains("quota exceeded") ||
                lower.Contains("free model limit") ||
                lower.Contains("exceeded") ||
                lower.Contains("credits"))
            {
                return true;
            }
        }
        return false;
    }

    private static string MaskKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "****";
        if (key.Length <= 8) return "****";
        return $"{key[..4]}...{key[^4..]}";
    }

    private static string? ExtractErrorMessage(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.TryGetProperty("error", out var errEl))
            {
                if (errEl.ValueKind == JsonValueKind.String) return errEl.GetString();
                if (errEl.TryGetProperty("message", out var msgEl)) return msgEl.GetString();
            }
        }
        catch { }
        return null;
    }

    private static async Task SendAnthropicErrorAsync(HttpListenerResponse resp, HttpStatusCode statusCode, string message, CancellationToken ct)
    {
        resp.ContentType = "application/json; charset=utf-8";
        resp.StatusCode = (int)statusCode;
        var errObj = new
        {
            type = "error",
            error = new
            {
                type = "api_error",
                message
            }
        };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(errObj));
        await resp.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
        resp.Close();
    }

    public void Dispose()
    {
        Stop();
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
