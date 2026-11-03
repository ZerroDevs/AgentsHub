using AgentsHub.Services;

var cases = new (string Latest, string Current, string Expect)[]
{
    ("v0.0.101", "0.0.100", "newer"),
    ("v0.0.100", "v0.0.101", "older"),
    ("v1.0.103", "v1.0.0", "newer"),
    ("v1.0.0", "0.0.100", "newer"),
    ("v1.0.5", "2.0.0", "older"),
    ("v0.0.102", "0.0.100", "newer"),
    ("0.0.100", "v1.0.0", "older"),
    ("1.0", "1.0.0", "equal"),
    ("v1.0.0.0", "1.0.0", "equal"),
    ("1.0.0-beta", "1.0.0", "older"),
    ("1.0.0", "1.0.0-beta", "newer"),
    ("1.0.0-beta.10", "1.0.0-beta.2", "newer"),
    ("01.02.03", "1.2.3", "equal"),
    ("release-1.2.3", "v1.2.3", "equal"),
    ("1.0.0+build.5", "1.0.0", "equal"),
    ("2.0.0", "2.0.0", "equal"),
};

int failures = 0;

foreach (var (latest, current, expect) in cases)
{
    bool okLatest = AppVersion.TryParse(latest, out var l);
    bool okCurrent = AppVersion.TryParse(current, out var c);
    string got = !okLatest || !okCurrent ? "unparsed"
        : l.IsNewerThan(c) ? "newer"
        : c.IsNewerThan(l) ? "older"
        : "equal";

    bool pass = got == expect && okLatest && okCurrent;
    if (!pass) failures++;
    Console.WriteLine($"{(pass ? "ok  " : "FAIL")} {latest,-22} vs {current,-14} -> {got} (expected {expect})");
}

foreach (string bad in new[] { "nightly", "", "   ", "v", "1.2.3.4.5" })
{
    bool parsed = AppVersion.TryParse(bad, out _);
    Console.WriteLine($"{(parsed ? "??  " : "ok  ")} unparseable '{bad}' -> parsed={parsed}");
    if (parsed) failures++;
}

string Json(params (string Tag, bool Draft, bool Pre, string? Asset)[] releases)
{
    var list = releases.Select(r => $$"""
        {"tag_name": "{{r.Tag}}", "draft": {{(r.Draft ? "true" : "false")}}, "prerelease": {{(r.Pre ? "true" : "false")}},
         "name": "Release {{r.Tag}}", "published_at": "2026-01-01T00:00:00Z",
         "assets": [{{(r.Asset == null ? "" : $$"""{"name": "{{r.Asset}}", "size": 123, "digest": "sha256:abc", "browser_download_url": "https://github.com/x/{{r.Asset}}"}""")}}]}
        """);
    return "[" + string.Join(",", list) + "]";
}

void Check(string name, bool result, UpdateInfo info, string expectVersion, string expectUrlContains = "")
{
    bool pass = result && info.LatestVersion == expectVersion
        && (expectUrlContains == "" || info.DownloadUrl.Contains(expectUrlContains, StringComparison.Ordinal));
    if (!pass) failures++;
    Console.WriteLine($"{(pass ? "ok  " : "FAIL")} {name}: picked v{info.LatestVersion} url={Path.GetFileName(info.DownloadUrl)}");
}

var info1 = new UpdateInfo();
Check("scrambled 0.0.100..102", UpdateService.ReadNewestRelease(Json(
    ("v0.0.100", false, false, "AgentsHub-Setup-v0.0.100.exe"),
    ("0.0.102", false, false, "AgentsHub-Setup-v0.0.102.exe"),
    ("v0.0.101", false, false, "AgentsHub-Setup-v0.0.101.exe")), info1), info1, "0.0.102", "0.0.102");

var info2 = new UpdateInfo();
Check("v1.0.0 published after v1.0.103", UpdateService.ReadNewestRelease(Json(
    ("v1.0.0", false, false, "AgentsHub-Setup-v1.0.0.exe"),
    ("v1.0.103", false, false, "AgentsHub-Setup-v1.0.103.exe")), info2), info2, "1.0.103", "1.0.103");

var info3 = new UpdateInfo();
Check("draft and prerelease skipped", UpdateService.ReadNewestRelease(Json(
    ("v9.9.9", true, false, null),
    ("v5.0.0", false, true, "AgentsHub-Setup-v5.0.0.exe"),
    ("v2.0.1", false, false, "AgentsHub-Setup-v2.0.1.exe")), info3), info3, "2.0.1", "2.0.1");

var info4 = new UpdateInfo();
Check("only prereleases", UpdateService.ReadNewestRelease(Json(
    ("v3.0.0-beta.2", false, true, "AgentsHub-Setup-v3.0.0-beta.2.exe"),
    ("v3.0.0-beta.10", false, true, "AgentsHub-Setup-v3.0.0-beta.10.exe")), info4), info4, "3.0.0-beta.10");

var info5 = new UpdateInfo();
bool none = UpdateService.ReadNewestRelease(Json(("nightly", false, false, null), ("release", false, false, null)), info5);
Console.WriteLine($"{(none ? "FAIL" : "ok  ")} unparseable tags -> {none}");
if (none) failures++;

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");

if (args.Contains("--live"))
{
    Console.WriteLine();
    var live = await UpdateService.Instance.CheckForUpdatesAsync();
    Console.WriteLine($"live: result={live.Result} latest=v{live.LatestVersion} current=v{live.CurrentVersion} ahead={live.InstalledIsAhead}");
    Console.WriteLine($"live: asset={(string.IsNullOrEmpty(live.DownloadUrl) ? "(none)" : Path.GetFileName(live.DownloadUrl))} urlTrusted={UpdateService.IsTrustedReleaseUrl(live.DownloadUrl)}");
    Console.WriteLine($"live: {live.Message}");
}
