using System.Net.Http;
using System.Text.Json;

namespace AgentsHub.Services.Byok;

/// <summary>
/// Reads the model list a provider publishes at `GET {baseUrl}/models`.
///
/// Every gateway that accepts an Anthropic-style key answers that route: OpenRouter, Vercel's AI
/// gateway, Together, Moonshot, and a local proxy. The shapes differ only in where the ids sit, so
/// `data[].id`, `data[].name`, `models[]` and a bare array of strings are all read.
/// </summary>
public static class ByokModels
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

    /// <summary>
    /// Reads the model list. `protocol` picks the auth header: an OpenAI-style gateway reads the
    /// bearer, an Anthropic endpoint reads x-api-key. Sending both is not harmless — OpenRouter drops
    /// from 450 models to a 20-entry stub when x-api-key rides along.
    /// </summary>
    public static async Task<List<string>> FetchAsync(
        string baseUrl,
        string apiKey,
        string protocol = "openai",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("The provider has no base URL. Add one first.");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Paste an API key before fetching its models.");
        }

        // Gateways disagree about the version prefix: `https://api.openrouter.ai/v1`, a bare host, and
        // a pasted `/chat/completions` link all describe the same place. Every plausible route is
        // tried, and the one that answered is what the error messages quote.
        string? rejection = null;
        foreach (string url in Candidates(baseUrl))
        {
            HttpResponseMessage response;
            string tried = url;

            try
            {
                (response, tried) = await GetAsync(url, apiKey, protocol, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                rejection = ex.Message;
                continue;
            }

            using (response)
            {
                string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                int code = (int)response.StatusCode;

                if (code is 401 or 403)
                {
                    // The key is the problem, not the path: another route would be refused the same way.
                    throw new InvalidOperationException(
                        $"{tried} rejected that key (HTTP {code}). Check the key, then fetch again.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    // A gateway that answers 404 with an HTML page says nothing a person can act on,
                    // so only a JSON error body is quoted.
                    string detail = body.Trim() is { Length: > 0 } trimmed && (trimmed[0] == '{' || trimmed[0] == '[')
                        ? ": " + (trimmed.Length > 200 ? trimmed[..200] + "…" : trimmed)
                        : string.Empty;

                    rejection = $"{tried} answered HTTP {code}{detail}";
                    continue;
                }

                List<string> models;
                try
                {
                    models = Parse(body);
                }
                catch (InvalidOperationException)
                {
                    rejection = $"{tried} answered with something that is not a model list";
                    continue;
                }

                if (models.Count > 0)
                {
                    return models;
                }

                rejection = $"{tried} answered but listed no models";
            }
        }

        throw new InvalidOperationException(
            (rejection ?? "The provider did not answer.")
            + $" Check the base URL — this was tried: {string.Join(", ", Candidates(baseUrl))}.");
    }

    /// <summary>
    /// The `/models` routes worth asking, most likely first: the URL as typed, then the same host
    /// with or without a `/v1` prefix.
    /// </summary>
    internal static List<string> Candidates(string baseUrl)
    {
        string trimmed = baseUrl.Trim();
        if (trimmed.Length == 0) return [];

        if (!trimmed.Contains("://", StringComparison.Ordinal)) trimmed = "https://" + trimmed;
        trimmed = trimmed.TrimEnd('/');

        foreach (string suffix in new[] { "/chat/completions", "/completions", "/messages" })
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[..^suffix.Length].TrimEnd('/');
                break;
            }
        }

        if (trimmed.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            return [trimmed];
        }

        var routes = new List<string> { trimmed + "/models" };

        routes.Add(trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^3].TrimEnd('/') + "/models"
            : trimmed + "/v1/models");

        return [.. routes.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static async Task<(HttpResponseMessage Response, string Url)> GetAsync(
        string url,
        string apiKey,
        string protocol,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        bool anthropic = string.Equals(protocol, "anthropic", StringComparison.OrdinalIgnoreCase)
            || url.Contains("api.anthropic.com", StringComparison.OrdinalIgnoreCase);

        if (anthropic)
        {
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey.Trim());
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
        else
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey.Trim()}");
        }

        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        try
        {
            var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            return (response, url);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Could not reach {url}: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"{url} did not answer within 25 seconds.");
        }
    }

    /// <summary>Every id found in the payload, de-duplicated and sorted.</summary>
    internal static List<string> Parse(string body)
    {
        var ids = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The provider answered with something that is not JSON.");
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                ReadArray(root, ids);
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (string key in new[] { "data", "models", "available_models" })
                {
                    if (root.TryGetProperty(key, out var list))
                    {
                        ReadArray(list, ids);
                    }
                }
            }
        }

        return [.. ids];
    }

    private static void ReadArray(JsonElement list, SortedSet<string> ids)
    {
        if (list.ValueKind != JsonValueKind.Array) return;

        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                Add(ids, item.GetString());
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object) continue;

            // `id` first: it is what the CLI accepts. A display name is only a fallback.
            if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                Add(ids, id.GetString());
            }
            else if (item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                Add(ids, name.GetString());
            }
        }
    }

    private static void Add(SortedSet<string> ids, string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length > 0) ids.Add(trimmed);
    }
}
