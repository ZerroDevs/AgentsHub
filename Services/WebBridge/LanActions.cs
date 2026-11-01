#nullable enable
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentsHub.Helpers;
using AgentsHub.Models;
#if WINDOWS
using System.Windows.Forms;
using AgentsHub.Forms;
using Microsoft.Web.WebView2.Core;
#endif

namespace AgentsHub.Services;

public sealed partial class DashboardWebBridge : IDisposable
{

    private Task HandleToggleLanProxyAsync(JsonElement data, string? requestId)
    {
        if (_lanProxyService != null)
        {
            bool enable;
            if (data.TryGetProperty("enabled", out var enProp))
            {
                enable = enProp.GetBoolean();
            }
            else
            {
                enable = !_lanProxyService.IsRunning;
            }

            if (enable)
            {
                var savedEnginePort = StartupHelper.GetIntPreference("EnginePort", HubProcessService.DefaultHubPort);
                var savedLanPort = StartupHelper.GetIntPreference("LanPort", 9191);
                _lanProxyService.Start(
                    savedLanPort,
                    savedEnginePort,
                    LanProxyService.ReadSelectedDirectPorts());
                StartupHelper.SetPreference("LanProxyEnabled", true);
            }
            else
            {
                _lanProxyService.Stop();
                StartupHelper.SetPreference("LanProxyEnabled", false);
            }

            var lanIp = NetworkHelper.GetLocalIPv4();
            var isRunning = _lanProxyService.IsRunning;
            var rPorts = GetRunningWebPorts(lanIp, _cliManager.HubService.Port, _lanProxyService.ListenPort);

            SendResponse(requestId, true, new
            {
                lanProxyEnabled = isRunning,
                lanProxyRunning = isRunning,
                runningPorts = rPorts
            });
        }
        else
        {
            SendResponse(requestId, false, "LAN Proxy service not initialized");
        }

        return Task.CompletedTask;
    }

    private Task HandleSetLanForwardersAsync(JsonElement data, string? requestId)
    {
        {
            if (_lanProxyService == null)
            {
                SendResponse(requestId, false, "LAN Proxy service not initialized");
                return Task.CompletedTask;
            }

            var chosen = new List<int>();
            if (data.TryGetProperty("ports", out var portsProp) && portsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in portsProp.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.Number && entry.TryGetInt32(out var port) && port is > 0 and <= 65535)
                        chosen.Add(port);
                }
            }

            LanProxyService.SaveSelectedDirectPorts(chosen);

            // A running proxy keeps its old sockets until it is rebuilt, so the change only
            // takes effect after a restart; the response carries the sockets that came up.
            if (_lanProxyService.IsRunning)
            {
                var savedEnginePort = StartupHelper.GetIntPreference("EnginePort", HubProcessService.DefaultHubPort);
                var savedLanPort = StartupHelper.GetIntPreference("LanPort", 9191);
                _lanProxyService.Stop();
                _lanProxyService.Start(
                    savedLanPort,
                    savedEnginePort,
                    LanProxyService.ReadSelectedDirectPorts());
            }

            SendResponse(requestId, true, new
            {
                lan = BuildLanStateDto(GetRequiredLanPorts())
            });
        }

        return Task.CompletedTask;
    }

    private async Task HandleGetLanStateAsync(JsonElement data, string? requestId)
    {
        // The firewall probe spawns PowerShell, so it is cached and only forced
        // when the UI asks for an explicit recheck.
        bool forceFirewall = data.TryGetProperty("refresh", out var refreshEl)
            && refreshEl.ValueKind == JsonValueKind.True;

        var required = GetRequiredLanPorts();

        if (!forceFirewall && _cachedFirewall != null)
        {
            SendResponse(requestId, true, new
            {
                lan = BuildLanStateDto(required),
                firewall = ToFirewallDto(_cachedFirewall, required),
                clients = ToClientDtos(_lanProxyService?.GetActiveClients())
            });
        }
        else if (!forceFirewall)
        {
            // Return LAN networking details immediately so WebView2 does not block
            SendResponse(requestId, true, new
            {
                lan = BuildLanStateDto(required),
                firewall = new
                {
                    querySucceeded = true,
                    state = "checking",
                    detail = "Probing Windows Firewall in background...",
                    requiredPorts = required
                },
                clients = ToClientDtos(_lanProxyService?.GetActiveClients())
            });

            // Run firewall probe in background and push update
            _ = Task.Run(async () =>
            {
                try
                {
                    var fw = await GetFirewallStatusAsync(required, force: false).ConfigureAwait(false);
                    PostEventToUi("lan_firewall_updated", ToFirewallDto(fw, required));
                }
                catch { }
            });
        }
        else
        {
            // User explicitly clicked "Recheck"
            var firewall = await GetFirewallStatusAsync(required, force: true).ConfigureAwait(false);

            SendResponse(requestId, true, new
            {
                lan = BuildLanStateDto(required),
                firewall = ToFirewallDto(firewall, required),
                clients = ToClientDtos(_lanProxyService?.GetActiveClients())
            });
        }
    }

    private Task HandleGetLanClientsAsync(string? requestId)
    {
        SendResponse(requestId, true, new
        {
            clients = ToClientDtos(_lanProxyService?.GetActiveClients())
        });

        return Task.CompletedTask;
    }

    private async Task HandleRequestFirewallRuleAsync(string? requestId)
    {
        var ports = GetRequiredLanPorts();
        if (ports.Count == 0)
        {
            SendResponse(requestId, false, "Turn LAN access on first, so there are ports to allow.");
            return;
        }

        var result = await FirewallHelper.RequestInboundRuleAsync(ports);
        var fresh = await GetFirewallStatusAsync(ports, force: true);

        SendResponse(requestId, true, new
        {
            succeeded = result.Succeeded,
            userCancelled = result.UserCancelled,
            message = result.Message,
            lan = BuildLanStateDto(ports),
            firewall = ToFirewallDto(fresh, ports)
        });
    }

    private async Task HandleRemoveFirewallRuleAsync(string? requestId)
    {
        var result = await FirewallHelper.RemoveInboundRuleAsync();
        var ports = GetRequiredLanPorts();
        var fresh = await GetFirewallStatusAsync(ports, force: true);

        SendResponse(requestId, true, new
        {
            succeeded = result.Succeeded,
            userCancelled = result.UserCancelled,
            message = result.Message,
            lan = BuildLanStateDto(ports),
            firewall = ToFirewallDto(fresh, ports)
        });
    }

    private async Task HandleGetPublicIpAsync(string? requestId)
    {
        var ipDetails = await NetworkHelper.FetchPublicIpDetailsAsync();
        SendResponse(requestId, true, ipDetails);
    }

    /// <summary>
    /// The ports that should be reachable from another device: the gateway plus every forwarder
    /// that actually bound. An unbound forwarder is excluded because something else owns that
    /// port, so allowing it through the firewall would achieve nothing.
    /// </summary>
    private IReadOnlyList<int> GetRequiredLanPorts()
    {
        var proxy = _lanProxyService;
        if (proxy == null || !proxy.IsRunning) return [];

        var ports = new List<int> { proxy.ListenPort };
        ports.AddRange(proxy.GetForwarders().Where(f => f.Bound).Select(f => f.Port));
        return [.. ports.Distinct().Order()];
    }

    private async Task<FirewallStatus> GetFirewallStatusAsync(IReadOnlyList<int> requiredPorts, bool force)
    {
        if (!force && _cachedFirewall is { } cached && (DateTime.UtcNow - _cachedFirewallAt).TotalMilliseconds < FirewallCacheMs)
        {
            return cached;
        }

        await _firewallGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!force && _cachedFirewall is { } recheck && (DateTime.UtcNow - _cachedFirewallAt).TotalMilliseconds < FirewallCacheMs)
            {
                return recheck;
            }

            var status = await FirewallHelper
                .GetStatusAsync(requiredPorts, NetworkHelper.GetLocalIPv4())
                .ConfigureAwait(false);

            _cachedFirewall = status;
            _cachedFirewallAt = DateTime.UtcNow;
            return status;
        }
        finally
        {
            _firewallGate.Release();
        }
    }

    private void InvalidateFirewallCache()
    {
        _cachedFirewall = null;
        _cachedFirewallAt = DateTime.MinValue;
    }

    private object BuildLanStateDto(IReadOnlyList<int> requiredPorts)
    {
        var proxy = _lanProxyService;
        var forwarders = proxy?.GetForwarders() ?? [];
        var lanIp = NetworkHelper.GetLocalIPv4();

        return new
        {
            lanIp,
            gatewayPort = proxy?.ListenPort ?? StartupHelper.GetIntPreference("LanPort", 9191),
            targetPort = proxy?.TargetPort ?? _cliManager.HubService.Port,
            running = proxy?.IsRunning ?? false,
            enabled = StartupHelper.GetPreference("LanProxyEnabled", false),
            bindError = proxy?.LastBindError ?? "",
            requiredPorts,
            selectedPorts = LanProxyService.ReadSelectedDirectPorts(),
            availableForwarders = LanProxyService
                .OfferedDirectPorts()
                .Select(port => new
                {
                    port,
                    label = LanProxyService.LabelForPort(port)
                })
                .ToArray(),
            forwarders = forwarders.Select(f => new
            {
                port = f.Port,
                label = f.Label,
                address = f.Address,
                bound = f.Bound,
                detail = f.Detail
            }).ToArray()
        };
    }

    private static object ToFirewallDto(FirewallStatus status, IReadOnlyList<int> requiredPorts)
    {
        return new
        {
            querySucceeded = status.QuerySucceeded,
            detail = status.Detail,
            state = status.StateFor(requiredPorts).ToString().ToLowerInvariant(),
            ruleExists = status.RuleExists,
            ruleEnabled = status.RuleEnabled,
            ruleInbound = status.RuleInbound,
            ruleAllow = status.RuleAllow,
            ruleProfile = status.RuleProfile,
            rulePorts = status.RulePorts,
            requiredPorts,
            missingPorts = status.MissingPorts(requiredPorts),
            domainEnabled = status.DomainEnabled,
            privateEnabled = status.PrivateEnabled,
            publicEnabled = status.PublicEnabled,
            activeProfile = status.ActiveProfile,
            firewallOnForActiveProfile = status.FirewallOnForActiveProfile,
            inboundBlocked = status.InboundBlockedByNetworkCategory,
            networkName = status.NetworkName,
            networkCategory = status.NetworkCategory,
            networkInterface = status.NetworkInterface
        };
    }

    private static object[] ToClientDtos(IReadOnlyList<ConnectedClientInfo>? clients)
    {
        if (clients == null || clients.Count == 0) return [];

        return [.. clients.Select(c => (object)new
        {
            ip = c.IpAddress,
            deviceName = c.DeviceName,
            hostName = c.HostName ?? "",
            userAgent = c.UserAgent ?? "",
            deviceType = LanProxyService.DetectDeviceType(c.UserAgent),
            activeConnections = c.ActiveConnections,
            firstSeen = c.FirstSeen.ToString("o"),
            lastActivity = c.LastActivity.ToString("o")
        })];
    }

    /// <summary>
    /// Remote devices need the gateway's pairing token; a loopback browser does not. The gateway
    /// exchanges it for a cookie on first use, so it appears in the URL only once.
    /// </summary>
    private string GatewayTokenParam =>
        _lanProxyService is { IsRunning: true } proxy && !string.IsNullOrEmpty(proxy.AccessToken)
            ? $"&k={proxy.AccessToken}"
            : string.Empty;

    private List<object> GetRunningWebPorts(string lanIp, int hubPort, int lanProxyPort)
    {
        string themeParam = $"?theme={(ThemeManager.CurrentTheme == AppTheme.Light ? "light" : "dark")}&accent={Uri.EscapeDataString(ThemeManager.AccentHex)}";
        var list = new List<object>
        {
            new
            {
                id = "hub",
                name = "Hub Dashboard",
                port = hubPort,
                lanPort = lanProxyPort,
                localUrl = $"http://127.0.0.1:{hubPort}/{themeParam}",
                lanUrl = $"http://{lanIp}:{lanProxyPort}/{themeParam}{GatewayTokenParam}",
                state = _cliManager.GetInfo(CliType.Hub).State.ToString(),
                isRunning = _cliManager.GetInfo(CliType.Hub).State == CliState.Running
            },
            new
            {
                id = "claude_code",
                name = "Claude Code Web",
                port = 9229,
                lanPort = 9229,
                localUrl = $"http://127.0.0.1:9229/{themeParam}",
                lanUrl = $"http://{lanIp}:9229/{themeParam}",
                state = "Running",
                isRunning = true
            }
        };

        var runningProfiles = _cliManager.GetRunningAntigravityProfiles();
        if (runningProfiles.Count > 0)
        {
            foreach (var prof in runningProfiles)
            {
                int pPort = _cliManager.GetPortForProfile(prof) ?? 9339;
                list.Add(new
                {
                    id = CliManagerService.FormatAntigravityProfileId(prof),
                    name = $"Antigravity CLI ({prof})",
                    port = pPort,
                    lanPort = pPort,
                    localUrl = $"http://127.0.0.1:{pPort}/{themeParam}",
                    lanUrl = $"http://{lanIp}:{pPort}/{themeParam}",
                    state = "Running",
                    isRunning = true
                });
            }
        }
        else
        {
            var activeProf = _cliManager.ProfileService.ActiveProfile ?? "Default";
            var activeInfo = _cliManager.GetInfo(CliType.AntigravityCli);
            int basePort = activeInfo.Port ?? 9339;
            bool isRunning = activeInfo.State == CliState.Running;
            list.Add(new
            {
                id = "antigravity_cli",
                name = $"Antigravity CLI ({activeProf})",
                port = basePort,
                lanPort = basePort,
                localUrl = $"http://127.0.0.1:{basePort}/{themeParam}",
                lanUrl = $"http://{lanIp}:{basePort}/{themeParam}",
                state = isRunning ? "Running" : "Offline",
                isRunning
            });
        }

        list.Add(new
        {
            id = "lan_proxy",
            name = "LAN Remote Gateway",
            port = lanProxyPort,
            lanPort = lanProxyPort,
            localUrl = $"http://127.0.0.1:{lanProxyPort}/{themeParam}",
            lanUrl = $"http://{lanIp}:{lanProxyPort}/{themeParam}{GatewayTokenParam}",
            state = (_lanProxyService?.IsRunning ?? false) ? "Running" : "Offline",
            isRunning = _lanProxyService?.IsRunning ?? false
        });

        var customWs = AiConfigService.Instance.CurrentConfig.CliWorkspaces;
        foreach (var ws in customWs)
        {
            if (ws.IsWebUi && ws.Port.HasValue && ws.Port.Value > 0)
            {
                int p = ws.Port.Value;
                list.Add(new
                {
                    id = ws.Id,
                    name = ws.Name,
                    port = p,
                    lanPort = p,
                    localUrl = $"http://127.0.0.1:{p}/",
                    lanUrl = $"http://{lanIp}:{p}/",
                    state = _cliManager.GetInfo(ws.Id).State.ToString(),
                    isRunning = _cliManager.GetInfo(ws.Id).State == CliState.Running
                });
            }
        }

        return list;
    }

}