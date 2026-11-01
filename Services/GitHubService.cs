#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AgentsHub.Models;

namespace AgentsHub.Services;

public sealed class GitHubService
{
    public static GitHubService Instance { get; } = new();

    private readonly HttpClient _httpClient;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string _authFilePath;

    private sealed class StoredGitHubAuth
    {
        public string Token { get; set; } = "";
        public string Username { get; set; } = "";
        public string? Name { get; set; }
        public string? AvatarUrl { get; set; }
        public string? HtmlUrl { get; set; }
        public string AuthMethod { get; set; } = "pat";
        public List<string> Scopes { get; set; } = [];
        public DateTime SavedAt { get; set; } = DateTime.UtcNow;
    }

    private GitHubService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "AgentsHub-Desktop");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentsHub");
        if (!Directory.Exists(appData))
        {
            Directory.CreateDirectory(appData);
        }
        _authFilePath = Path.Combine(appData, "github_auth.json");
    }

    public async Task<GitHubAuthStatus> GetAuthStatusAsync(CancellationToken ct = default)
    {
        // 1. Check stored PAT first
        if (File.Exists(_authFilePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(_authFilePath, ct).ConfigureAwait(false);
                var stored = JsonSerializer.Deserialize<StoredGitHubAuth>(json, JsonOptions);
                if (stored != null && !string.IsNullOrWhiteSpace(stored.Token))
                {
                    // Validate token with live API
                    using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", stored.Token);

                    using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
                    if (res.IsSuccessStatusCode)
                    {
                        var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(body);
                        var root = doc.RootElement;

                        var scopes = new List<string>();
                        if (res.Headers.TryGetValues("X-OAuth-Scopes", out var scopeValues))
                        {
                            foreach (var sv in scopeValues)
                            {
                                foreach (var part in sv.Split(',', StringSplitOptions.RemoveEmptyEntries))
                                {
                                    scopes.Add(part.Trim());
                                }
                            }
                        }

                        return new GitHubAuthStatus
                        {
                            IsAuthenticated = true,
                            AuthMethod = stored.AuthMethod ?? "pat",
                            Username = root.TryGetProperty("login", out var lProp) ? lProp.GetString() : stored.Username,
                            Name = root.TryGetProperty("name", out var nProp) ? nProp.GetString() : stored.Name,
                            AvatarUrl = root.TryGetProperty("avatar_url", out var aProp) ? aProp.GetString() : stored.AvatarUrl,
                            HtmlUrl = root.TryGetProperty("html_url", out var hProp) ? hProp.GetString() : stored.HtmlUrl,
                            Scopes = scopes.Count > 0 ? scopes : stored.Scopes
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GitHubService] PAT validation error: {ex.Message}");
            }
        }

        // 2. Check GitHub CLI (gh auth status / api user)
        var cliStatus = await CheckGitHubCliAuthAsync(ct).ConfigureAwait(false);
        if (cliStatus.IsAuthenticated)
        {
            return cliStatus;
        }

        // 3. Auto-check Git Credential Manager (GCM)
        var gcmStatus = await CheckGitCredentialManagerAuthAsync(ct).ConfigureAwait(false);
        if (gcmStatus.IsAuthenticated)
        {
            return gcmStatus;
        }

        return new GitHubAuthStatus
        {
            IsAuthenticated = false,
            AuthMethod = "none"
        };
    }

    public async Task<(bool success, string message, GitHubAuthStatus? status)> AutoConnectFromGitAsync(CancellationToken ct = default)
    {
        var gcmStatus = await CheckGitCredentialManagerAuthAsync(ct).ConfigureAwait(false);
        if (gcmStatus.IsAuthenticated)
        {
            return (true, $"Auto-connected to GitHub as @{gcmStatus.Username} via Git Credential Manager.", gcmStatus);
        }

        return (false, "No active GitHub credentials found in Git Credential Manager. You can generate a token on GitHub.", null);
    }

    public async Task<(bool success, string message, GitHubAuthStatus? status)> SavePatAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return (false, "Token cannot be empty.", null);
        }

        token = token.Trim();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                return (false, $"GitHub rejected token (HTTP {(int)res.StatusCode}: {res.ReasonPhrase}).", null);
            }

            var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var username = root.TryGetProperty("login", out var lProp) ? lProp.GetString() ?? "" : "";
            var name = root.TryGetProperty("name", out var nProp) ? nProp.GetString() : null;
            var avatarUrl = root.TryGetProperty("avatar_url", out var aProp) ? aProp.GetString() : null;
            var htmlUrl = root.TryGetProperty("html_url", out var hProp) ? hProp.GetString() : null;

            var scopes = new List<string>();
            if (res.Headers.TryGetValues("X-OAuth-Scopes", out var scopeValues))
            {
                foreach (var sv in scopeValues)
                {
                    foreach (var part in sv.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        scopes.Add(part.Trim());
                    }
                }
            }

            var stored = new StoredGitHubAuth
            {
                Token = token,
                Username = username,
                Name = name,
                AvatarUrl = avatarUrl,
                HtmlUrl = htmlUrl,
                Scopes = scopes,
                SavedAt = DateTime.UtcNow
            };

            await File.WriteAllTextAsync(_authFilePath, JsonSerializer.Serialize(stored, JsonOptions), ct).ConfigureAwait(false);

            var status = new GitHubAuthStatus
            {
                IsAuthenticated = true,
                AuthMethod = "pat",
                Username = username,
                Name = name,
                AvatarUrl = avatarUrl,
                HtmlUrl = htmlUrl,
                Scopes = scopes
            };

            return (true, $"Authenticated successfully as @{username}", status);
        }
        catch (Exception ex)
        {
            return (false, $"Connection error: {ex.Message}", null);
        }
    }

    public bool Disconnect()
    {
        try
        {
            if (File.Exists(_authFilePath))
            {
                File.Delete(_authFilePath);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string?> GetEffectiveTokenAsync(CancellationToken ct = default)
    {
        // Check stored PAT
        if (File.Exists(_authFilePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(_authFilePath, ct).ConfigureAwait(false);
                var stored = JsonSerializer.Deserialize<StoredGitHubAuth>(json, JsonOptions);
                if (stored != null && !string.IsNullOrWhiteSpace(stored.Token))
                {
                    return stored.Token;
                }
            }
            catch { }
        }

        // Check CLI token
        var (rc, tokenOut) = await RunGhCliAsync(["auth", "token"], ct).ConfigureAwait(false);
        if (rc == 0 && !string.IsNullOrWhiteSpace(tokenOut))
        {
            return tokenOut.Trim();
        }

        return null;
    }

    public async Task<GitHubPublishResult> PublishRepositoryAsync(
        string repoPath,
        string repoName,
        string description,
        bool isPrivate,
        CancellationToken ct = default)
    {
        var token = await GetEffectiveTokenAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token))
        {
            return new GitHubPublishResult
            {
                Success = false,
                Message = "Not authenticated with GitHub. Please link a GitHub PAT or log in with GitHub CLI ('gh auth login')."
            };
        }

        if (string.IsNullOrWhiteSpace(repoName))
        {
            repoName = Path.GetFileName(repoPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        try
        {
            // 1. Create repository via GitHub REST API
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/user/repos");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var payload = new
            {
                name = repoName,
                description = description ?? "",
                @private = isPrivate,
                auto_init = false
            };

            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            var resBody = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!res.IsSuccessStatusCode)
            {
                return new GitHubPublishResult
                {
                    Success = false,
                    Message = $"GitHub API error (HTTP {(int)res.StatusCode}): {resBody}"
                };
            }

            using var doc = JsonDocument.Parse(resBody);
            var root = doc.RootElement;
            var htmlUrl = root.TryGetProperty("html_url", out var hProp) ? hProp.GetString() : null;
            var cloneUrl = root.TryGetProperty("clone_url", out var cProp) ? cProp.GetString() : null;

            if (string.IsNullOrEmpty(cloneUrl))
            {
                return new GitHubPublishResult
                {
                    Success = false,
                    Message = "Created remote repository, but clone URL was not returned by GitHub."
                };
            }

            // 2. Link remote origin locally
            var (remRc, remOut) = await GitRepositoryManagerService.RunGitAsync(repoPath, ["remote", "add", "origin", cloneUrl], ct).ConfigureAwait(false);
            if (remRc != 0 && remOut.Contains("remote origin already exists", StringComparison.OrdinalIgnoreCase))
            {
                await GitRepositoryManagerService.RunGitAsync(repoPath, ["remote", "set-url", "origin", cloneUrl], ct).ConfigureAwait(false);
            }

            // 3. Detect current branch
            var (brRc, brOut) = await GitRepositoryManagerService.RunGitAsync(repoPath, ["rev-parse", "--abbrev-ref", "HEAD"], ct).ConfigureAwait(false);
            string branch = brRc == 0 && !string.IsNullOrWhiteSpace(brOut) ? brOut.Trim() : "main";

            // 4. Push initial branch upstream
            var (pushRc, pushOut) = await GitRepositoryManagerService.RunGitAsync(repoPath, ["push", "-u", "origin", branch], ct, timeoutSeconds: 45).ConfigureAwait(false);

            return new GitHubPublishResult
            {
                Success = true,
                RepoUrl = htmlUrl,
                CloneUrl = cloneUrl,
                Message = pushRc == 0
                    ? $"Repository published and branch '{branch}' pushed to GitHub."
                    : $"Remote repository created at {htmlUrl}, but push requires manual sync: {pushOut}"
            };
        }
        catch (Exception ex)
        {
            return new GitHubPublishResult
            {
                Success = false,
                Message = $"Failed to publish repository: {ex.Message}"
            };
        }
    }

    public static string? NormalizeGitHubWebUrl(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl)) return null;

        var url = remoteUrl.Trim();

        // git@github.com:owner/repo.git or ssh://git@github.com/owner/repo.git
        var sshMatch = Regex.Match(url, @"^(?:ssh:\/\/)?git@github\.com[:\/]([A-Za-z0-9_.\-]+)\/([A-Za-z0-9_.\-]+?)(?:\.git)?$");
        if (sshMatch.Success)
        {
            return $"https://github.com/{sshMatch.Groups[1].Value}/{sshMatch.Groups[2].Value}";
        }

        // https://github.com/owner/repo.git or https://github.com/owner/repo
        var httpsMatch = Regex.Match(url, @"^https?:\/\/(?:[A-Za-z0-9_.\-]+@)?github\.com\/([A-Za-z0-9_.\-]+)\/([A-Za-z0-9_.\-]+?)(?:\.git)?\/?$");
        if (httpsMatch.Success)
        {
            return $"https://github.com/{httpsMatch.Groups[1].Value}/{httpsMatch.Groups[2].Value}";
        }

        return null;
    }

    public static string? GetGitHubBranchUrl(string? githubWebUrl, string? branch)
    {
        if (string.IsNullOrWhiteSpace(githubWebUrl)) return null;
        if (string.IsNullOrWhiteSpace(branch) || branch == "HEAD" || branch == "detached") return githubWebUrl;
        return $"{githubWebUrl.TrimEnd('/')}/tree/{Uri.EscapeDataString(branch)}";
    }

    public static (string owner, string repo)? ParseGitHubOwnerAndRepo(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl)) return null;
        var url = remoteUrl.Trim();
        var sshMatch = Regex.Match(url, @"^(?:ssh:\/\/)?git@github\.com[:\/]([A-Za-z0-9_.\-]+)\/([A-Za-z0-9_.\-]+?)(?:\.git)?$");
        if (sshMatch.Success) return (sshMatch.Groups[1].Value, sshMatch.Groups[2].Value);
        var httpsMatch = Regex.Match(url, @"^https?:\/\/(?:[A-Za-z0-9_.\-]+@)?github\.com\/([A-Za-z0-9_.\-]+)\/([A-Za-z0-9_.\-]+?)(?:\.git)?\/?$");
        if (httpsMatch.Success) return (httpsMatch.Groups[1].Value, httpsMatch.Groups[2].Value);
        return null;
    }

    public async Task<GitHubPullRequestListResult> GetPullRequestsAsync(string repoPath, string state = "open", CancellationToken ct = default)
    {
        var result = new GitHubPullRequestListResult();
        var (rc, remUrl) = await GitRepositoryManagerService.RunGitAsync(repoPath, ["remote", "get-url", "origin"], ct).ConfigureAwait(false);
        if (rc != 0 || string.IsNullOrWhiteSpace(remUrl))
        {
            result.Success = false;
            result.Message = "No 'origin' remote found for this repository.";
            return result;
        }

        var parsed = ParseGitHubOwnerAndRepo(remUrl);
        if (parsed == null)
        {
            result.Success = false;
            result.Message = "Origin remote is not a recognized GitHub URL.";
            result.RepoUrl = remUrl;
            return result;
        }

        var (owner, repo) = parsed.Value;
        result.RepoUrl = $"https://github.com/{owner}/{repo}";
        string apiUrl = $"https://api.github.com/repos/{owner}/{repo}/pulls?state={Uri.EscapeDataString(state)}&per_page=30";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            var token = await GetEffectiveTokenAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(token))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                result.Success = false;
                result.Message = $"GitHub API returned {(int)res.StatusCode}: {res.ReasonPhrase}";
                return result;
            }

            var json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                result.Success = true;
                result.Message = "No pull requests found.";
                return result;
            }

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var pr = new GitHubPullRequest
                {
                    Number = el.TryGetProperty("number", out var numEl) ? numEl.GetInt32() : 0,
                    Title = el.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "" : "",
                    Body = el.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() : null,
                    State = el.TryGetProperty("state", out var stateEl) ? stateEl.GetString() ?? "open" : "open",
                    IsDraft = el.TryGetProperty("draft", out var draftEl) && draftEl.GetBoolean(),
                    HtmlUrl = el.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() : null
                };

                if (el.TryGetProperty("user", out var userEl))
                {
                    pr.UserLogin = userEl.TryGetProperty("login", out var ul) ? ul.GetString() ?? "" : "";
                    pr.UserAvatar = userEl.TryGetProperty("avatar_url", out var ua) ? ua.GetString() : null;
                }

                if (el.TryGetProperty("head", out var headEl))
                {
                    pr.HeadRef = headEl.TryGetProperty("ref", out var hr) ? hr.GetString() ?? "" : "";
                    pr.HeadSha = headEl.TryGetProperty("sha", out var hs) ? hs.GetString() : null;
                }

                if (el.TryGetProperty("base", out var baseEl))
                {
                    pr.BaseRef = baseEl.TryGetProperty("ref", out var br) ? br.GetString() ?? "" : "";
                }

                if (el.TryGetProperty("created_at", out var createdEl) && DateTime.TryParse(createdEl.GetString(), out var dt))
                {
                    pr.CreatedAt = dt;
                }

                if (el.TryGetProperty("updated_at", out var updEl) && DateTime.TryParse(updEl.GetString(), out var udt))
                {
                    pr.UpdatedAt = udt;
                }

                if (el.TryGetProperty("labels", out var labelsEl) && labelsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var lbl in labelsEl.EnumerateArray())
                    {
                        if (lbl.TryGetProperty("name", out var n))
                        {
                            var nameStr = n.GetString();
                            if (!string.IsNullOrWhiteSpace(nameStr)) pr.Labels.Add(nameStr);
                        }
                    }
                }

                result.PullRequests.Add(pr);
            }

            result.Success = true;
            result.Message = $"Loaded {result.PullRequests.Count} pull requests.";
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"Error fetching pull requests: {ex.Message}";
            return result;
        }
    }

    public async Task<(bool success, string message, string branchName)> CheckoutPullRequestAsync(string repoPath, int prNumber, CancellationToken ct = default)
    {
        string targetBranch = $"pr-{prNumber}";
        var (fetchRc, fetchOut) = await GitRepositoryManagerService.RunGitAsync(repoPath, ["fetch", "origin", $"pull/{prNumber}/head:{targetBranch}"], ct).ConfigureAwait(false);
        if (fetchRc != 0)
        {
            var (fRc2, fOut2) = await GitRepositoryManagerService.RunGitAsync(repoPath, ["fetch", "origin", $"pull/{prNumber}/head"], ct).ConfigureAwait(false);
            if (fRc2 != 0)
            {
                return (false, $"Failed to fetch PR #{prNumber}: {fetchOut}\n{fOut2}", targetBranch);
            }
            var (coRc2, coOut2) = await GitRepositoryManagerService.RunGitAsync(repoPath, ["checkout", "-B", targetBranch, "FETCH_HEAD"], ct).ConfigureAwait(false);
            return (coRc2 == 0, coRc2 == 0 ? $"Checked out PR #{prNumber} as branch '{targetBranch}'" : coOut2, targetBranch);
        }

        var (coRc, coOut) = await GitRepositoryManagerService.RunGitAsync(repoPath, ["checkout", targetBranch], ct).ConfigureAwait(false);
        return (coRc == 0, coRc == 0 ? $"Checked out PR #{prNumber} as branch '{targetBranch}'" : coOut, targetBranch);
    }

    public async Task<(bool success, string message, string? sha)> MergePullRequestAsync(
        string repoPath,
        int prNumber,
        string mergeMethod = "merge",
        string? commitTitle = null,
        string? commitMessage = null,
        CancellationToken ct = default)
    {
        var token = await GetEffectiveTokenAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            return (false, "GitHub Personal Access Token required to merge pull requests. Connect in Settings or GitHub tab.", null);
        }

        var (rc, remUrl) = await GitRepositoryManagerService.RunGitAsync(repoPath, ["remote", "get-url", "origin"], ct).ConfigureAwait(false);
        if (rc != 0 || string.IsNullOrWhiteSpace(remUrl))
        {
            return (false, "No 'origin' remote found for this repository.", null);
        }

        var parsed = ParseGitHubOwnerAndRepo(remUrl);
        if (parsed == null)
        {
            return (false, "Origin remote is not a recognized GitHub URL.", null);
        }

        var (owner, repo) = parsed.Value;
        string apiUrl = $"https://api.github.com/repos/{owner}/{repo}/pulls/{prNumber}/merge";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Put, apiUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var payload = new Dictionary<string, object?>
            {
                ["merge_method"] = string.IsNullOrWhiteSpace(mergeMethod) ? "merge" : mergeMethod.ToLowerInvariant()
            };
            if (!string.IsNullOrWhiteSpace(commitTitle)) payload["commit_title"] = commitTitle;
            if (!string.IsNullOrWhiteSpace(commitMessage)) payload["commit_message"] = commitMessage;

            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!res.IsSuccessStatusCode)
            {
                return (false, $"GitHub rejected merge ({(int)res.StatusCode}): {body}", null);
            }

            using var doc = JsonDocument.Parse(body);
            string? sha = doc.RootElement.TryGetProperty("sha", out var s) ? s.GetString() : null;
            string msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "PR merged successfully" : "PR merged successfully";

            return (true, msg, sha);
        }
        catch (Exception ex)
        {
            return (false, $"Merge failed: {ex.Message}", null);
        }
    }

    public async Task<(bool success, string subject, string body, string fullMessage)> GenerateCommitMessageFromDiffAsync(
        string repoPath,
        CancellationToken ct = default)
    {
        // 1. Get staged changes first
        var (_, statOut) = await GitRepositoryManagerService.RunGitAsync(repoPath, ["diff", "--cached", "--stat"], ct).ConfigureAwait(false);
        var (_, patchOut) = await GitRepositoryManagerService.RunGitAsync(repoPath, ["diff", "--cached", "--no-color", "--unified=1"], ct).ConfigureAwait(false);

        // If nothing staged, inspect unstaged changes
        if (string.IsNullOrWhiteSpace(patchOut))
        {
            var (_, uStat) = await GitRepositoryManagerService.RunGitAsync(repoPath, ["diff", "--stat"], ct).ConfigureAwait(false);
            var (_, uDiff) = await GitRepositoryManagerService.RunGitAsync(repoPath, ["diff", "--no-color", "--unified=1"], ct).ConfigureAwait(false);
            statOut = uStat;
            patchOut = uDiff;
        }

        if (string.IsNullOrWhiteSpace(patchOut))
        {
            return (true, "chore: update working directory", "", "chore: update working directory");
        }

        // 2. Parse changed files to deduce primary scope and type
        var (_, nameStatus) = await GitRepositoryManagerService.RunGitAsync(repoPath, ["diff", "--cached", "--name-status"], ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(nameStatus))
        {
            var (_, uNameStatus) = await GitRepositoryManagerService.RunGitAsync(repoPath, ["diff", "--name-status"], ct).ConfigureAwait(false);
            nameStatus = uNameStatus;
        }

        var lines = (nameStatus ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var files = new List<(char status, string path)>();
        foreach (var line in lines)
        {
            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                char st = char.ToUpperInvariant(parts[0][0]);
                files.Add((st, parts[1].Trim()));
            }
        }

        string type = "feat";
        string scope = "ui";
        var bulletPoints = new List<string>();

        bool allDocs = files.Count > 0 && files.All(f => f.path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || f.path.StartsWith("docs", StringComparison.OrdinalIgnoreCase));
        bool allStyle = files.Count > 0 && files.All(f => f.path.EndsWith(".css", StringComparison.OrdinalIgnoreCase));
        bool hasTests = files.Any(f => f.path.Contains("test", StringComparison.OrdinalIgnoreCase));
        bool hasBuild = files.Any(f => f.path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || f.path.EndsWith(".iss", StringComparison.OrdinalIgnoreCase) || f.path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase));
        bool hasDeleteOnly = files.Count > 0 && files.All(f => f.status == 'D');

        if (allDocs)
        {
            type = "docs";
            scope = "readme";
        }
        else if (allStyle)
        {
            type = "style";
            scope = "theme";
        }
        else if (hasTests)
        {
            type = "test";
            scope = "unit";
        }
        else if (hasBuild)
        {
            type = "build";
            scope = "deps";
        }
        else if (hasDeleteOnly)
        {
            type = "refactor";
            scope = "clean";
        }
        else if (patchOut.Contains("fix", StringComparison.OrdinalIgnoreCase) || patchOut.Contains("bug", StringComparison.OrdinalIgnoreCase) || patchOut.Contains("error", StringComparison.OrdinalIgnoreCase) || patchOut.Contains("exception", StringComparison.OrdinalIgnoreCase))
        {
            type = "fix";
        }

        if (files.Count > 0 && !allDocs && !allStyle)
        {
            var first = files[0].path.ToLowerInvariant();
            if (first.StartsWith("services/") || first.StartsWith("models/") || first.StartsWith("helpers/"))
                scope = "core";
            else if (first.Contains("git") || first.Contains("repo"))
                scope = "git";
            else if (first.Contains("settings"))
                scope = "settings";
            else if (first.Contains("terminal"))
                scope = "terminals";
            else if (first.Contains("quota") || first.Contains("network"))
                scope = "network";
            else if (first.StartsWith("ui/"))
                scope = "ui";
        }

        foreach (var (st, path) in files.Take(6))
        {
            string verb = st switch
            {
                'A' => "Add",
                'D' => "Remove",
                'M' => "Update",
                'R' => "Rename",
                _ => "Modify"
            };
            string filename = Path.GetFileName(path);
            bulletPoints.Add($"- {verb} {filename}");
        }
        if (files.Count > 6)
        {
            bulletPoints.Add($"- Update {files.Count - 6} other files");
        }

        string subject = $"{type}({scope}): update {scope} components";
        if (files.Count == 1 && bulletPoints.Count > 0)
        {
            subject = $"{type}({scope}): {bulletPoints[0][2..].ToLowerInvariant()}";
        }
        else if (allDocs)
        {
            subject = "docs: update documentation and project specifications";
        }

        string body = string.Join("\n", bulletPoints);
        string fullMsg = string.IsNullOrWhiteSpace(body) ? subject : $"{subject}\n\n{body}";

        return (true, subject, body, fullMsg);
    }

    private async Task<GitHubAuthStatus> CheckGitHubCliAuthAsync(CancellationToken ct)
    {
        var (rc, output) = await RunGhCliAsync(["api", "user"], ct).ConfigureAwait(false);
        if (rc == 0 && !string.IsNullOrWhiteSpace(output) && output.TrimStart().StartsWith("{"))
        {
            try
            {
                using var doc = JsonDocument.Parse(output);
                var root = doc.RootElement;
                if (root.TryGetProperty("login", out var lProp))
                {
                    return new GitHubAuthStatus
                    {
                        IsAuthenticated = true,
                        AuthMethod = "cli",
                        Username = lProp.GetString(),
                        Name = root.TryGetProperty("name", out var nProp) ? nProp.GetString() : null,
                        AvatarUrl = root.TryGetProperty("avatar_url", out var aProp) ? aProp.GetString() : null,
                        HtmlUrl = root.TryGetProperty("html_url", out var hProp) ? hProp.GetString() : null,
                        Scopes = ["repo", "read:user"]
                    };
                }
            }
            catch { }
        }

        return new GitHubAuthStatus { IsAuthenticated = false, AuthMethod = "none" };
    }

    private static async Task<(int exitCode, string output)> RunGhCliAsync(string[] args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "gh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            var outTask = proc.StandardOutput.ReadToEndAsync(ct);
            var errTask = proc.StandardError.ReadToEndAsync(ct);

            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var stdout = await outTask.ConfigureAwait(false);
            var stderr = await errTask.ConfigureAwait(false);

            var combined = (stdout + "\n" + stderr).Trim();
            return (proc.ExitCode, combined);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private async Task<GitHubAuthStatus> CheckGitCredentialManagerAuthAsync(CancellationToken ct)
    {
        try
        {
            var (rc, output) = await RunGitWithStdinAsync(["credential", "fill"], "protocol=https\nhost=github.com\n\n", ct).ConfigureAwait(false);
            if (rc == 0 && !string.IsNullOrWhiteSpace(output))
            {
                string? token = null;
                string? username = null;
                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var line in lines)
                {
                    if (line.StartsWith("password=", StringComparison.OrdinalIgnoreCase))
                    {
                        token = line["password=".Length..].Trim();
                    }
                    else if (line.StartsWith("username=", StringComparison.OrdinalIgnoreCase))
                    {
                        username = line["username=".Length..].Trim();
                    }
                }

                if (!string.IsNullOrWhiteSpace(token))
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                    using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
                    if (res.IsSuccessStatusCode)
                    {
                        var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(body);
                        var root = doc.RootElement;

                        var login = root.TryGetProperty("login", out var lProp) ? lProp.GetString() ?? username ?? "" : username ?? "";
                        var name = root.TryGetProperty("name", out var nProp) ? nProp.GetString() : null;
                        var avatarUrl = root.TryGetProperty("avatar_url", out var aProp) ? aProp.GetString() : null;
                        var htmlUrl = root.TryGetProperty("html_url", out var hProp) ? hProp.GetString() : null;

                        var scopes = new List<string>();
                        if (res.Headers.TryGetValues("X-OAuth-Scopes", out var scopeValues))
                        {
                            foreach (var sv in scopeValues)
                            {
                                foreach (var part in sv.Split(',', StringSplitOptions.RemoveEmptyEntries))
                                {
                                    scopes.Add(part.Trim());
                                }
                            }
                        }

                        // Auto-persist so subsequent queries don't have to query git credential fill every time
                        var stored = new StoredGitHubAuth
                        {
                            Token = token,
                            Username = login,
                            Name = name,
                            AvatarUrl = avatarUrl,
                            HtmlUrl = htmlUrl,
                            AuthMethod = "gcm",
                            Scopes = scopes.Count > 0 ? scopes : ["repo", "workflow"],
                            SavedAt = DateTime.UtcNow
                        };
                        await File.WriteAllTextAsync(_authFilePath, JsonSerializer.Serialize(stored, JsonOptions), ct).ConfigureAwait(false);

                        return new GitHubAuthStatus
                        {
                            IsAuthenticated = true,
                            AuthMethod = "gcm",
                            Username = login,
                            Name = name,
                            AvatarUrl = avatarUrl,
                            HtmlUrl = htmlUrl,
                            Scopes = stored.Scopes
                        };
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GitHubService] GCM check error: {ex.Message}");
        }

        return new GitHubAuthStatus { IsAuthenticated = false, AuthMethod = "none" };
    }

    private static async Task<(int returnCode, string output)> RunGitWithStdinAsync(string[] args, string stdinContent, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            if (!string.IsNullOrEmpty(stdinContent))
            {
                await proc.StandardInput.WriteAsync(stdinContent).ConfigureAwait(false);
                proc.StandardInput.Close();
            }

            var outTask = proc.StandardOutput.ReadToEndAsync(ct);
            var errTask = proc.StandardError.ReadToEndAsync(ct);

            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var stdout = await outTask.ConfigureAwait(false);
            var stderr = await errTask.ConfigureAwait(false);

            return (proc.ExitCode, (stdout + "\n" + stderr).Trim());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
