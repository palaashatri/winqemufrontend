using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace WinQemu.Qemu.GuestAgent;

public sealed record GuestNetworkInterfaceInfo(
    string Name,
    string HardwareAddress,
    IReadOnlyList<string> IpAddresses);

/// <summary>
/// Optional QEMU Guest Agent (QGA) client for guest-aware integration (§36).
/// Separate from QMP as required by §9.
/// </summary>
public sealed class QgaClient
{
    private readonly int _port;

    public QgaClient(int port)
    {
        _port = port;
    }

    public async Task<bool> PingAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var resp = await ExecuteCommandAsync("guest-ping", null, timeout ?? TimeSpan.FromMilliseconds(800), cancellationToken)
                .ConfigureAwait(false);
            return resp is not null && resp.ContainsKey("return");
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<GuestNetworkInterfaceInfo>> GetNetworkInterfacesAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<GuestNetworkInterfaceInfo>();
        try
        {
            var resp = await ExecuteCommandAsync("guest-network-get-interfaces", null, TimeSpan.FromSeconds(2), cancellationToken)
                .ConfigureAwait(false);
            if (resp?["return"] is not JsonArray arr)
            {
                return results;
            }

            foreach (var item in arr.OfType<JsonObject>())
            {
                var name = item["name"]?.GetValue<string>() ?? "eth0";
                var mac = item["hardware-address"]?.GetValue<string>() ?? string.Empty;
                var ips = new List<string>();
                if (item["ip-addresses"] is JsonArray ipArr)
                {
                    foreach (var ipObj in ipArr.OfType<JsonObject>())
                    {
                        var addr = ipObj["ip-address"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(addr) && addr != "127.0.0.1" && addr != "::1")
                        {
                            ips.Add(addr);
                        }
                    }
                }
                results.Add(new GuestNetworkInterfaceInfo(name, mac, ips));
            }
        }
        catch
        {
        }

        return results;
    }

    public async Task<bool> RequestShutdownAsync(string mode = "powerdown", CancellationToken cancellationToken = default)
    {
        try
        {
            var args = new JsonObject { ["mode"] = mode };
            await ExecuteCommandAsync("guest-shutdown", args, TimeSpan.FromSeconds(2), cancellationToken, expectResponse: false).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> FsFreezeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var resp = await ExecuteCommandAsync("guest-fsfreeze-freeze", null, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            return resp?["return"] is not null;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> FsThawAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var resp = await ExecuteCommandAsync("guest-fsfreeze-thaw", null, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            return resp?["return"] is not null;
        }
        catch
        {
            return false;
        }
    }

    private async Task<JsonObject?> ExecuteCommandAsync(
        string command,
        JsonObject? arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool expectResponse = true)
    {
        if (_port <= 0)
        {
            return null;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port, timeoutCts.Token).ConfigureAwait(false);
        await using var stream = client.GetStream();
        var utf8 = new UTF8Encoding(false);
        using var reader = new StreamReader(stream, utf8, false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(stream, utf8, 4096, leaveOpen: true) { AutoFlush = true };

        var req = new JsonObject { ["execute"] = command };
        if (arguments is not null)
        {
            req["arguments"] = arguments;
        }

        await writer.WriteLineAsync(req.ToJsonString().AsMemory(), timeoutCts.Token).ConfigureAwait(false);
        if (!expectResponse)
        {
            return null;
        }

        var line = await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(line) ? null : JsonNode.Parse(line) as JsonObject;
    }
}
