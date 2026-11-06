using System.Net;
using System.Net.Sockets;
using WinQemu.Core.Models;

namespace WinQemu.Networking;

public sealed record PortForwardValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Validates networking and port-forwarding rules (§25, §26, §27).
/// Ensures safe user-mode NAT defaults and checks for host port collisions.
/// </summary>
public static class PortForwardingManager
{
    public static PortForwardValidationResult ValidateRules(
        IReadOnlyList<PortForwardRule> rules,
        bool checkHostPortAvailability = false)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var errors = new List<string>();
        var warnings = new List<string>();
        var seenHostPorts = new HashSet<(string Proto, int Port)>();

        foreach (var rule in rules)
        {
            if (!rule.Enabled)
            {
                continue;
            }

            var proto = string.IsNullOrWhiteSpace(rule.Protocol) ? "tcp" : rule.Protocol.Trim().ToLowerInvariant();
            if (proto is not ("tcp" or "udp"))
            {
                errors.Add($"Rule '{rule.Name}' uses unsupported protocol '{rule.Protocol}'. Use TCP or UDP.");
                continue;
            }

            if (rule.HostPort is <= 0 or > 65535)
            {
                errors.Add($"Rule '{rule.Name}' has invalid host port {rule.HostPort} (must be 1–65535).");
                continue;
            }

            if (rule.GuestPort is <= 0 or > 65535)
            {
                errors.Add($"Rule '{rule.Name}' has invalid guest port {rule.GuestPort} (must be 1–65535).");
                continue;
            }

            if (rule.HostPort < 1024)
            {
                warnings.Add($"Rule '{rule.Name}' uses privileged host port {rule.HostPort}. Prefer ports 1024–65535 when running unelevated.");
            }

            if (!seenHostPorts.Add((proto, rule.HostPort)))
            {
                errors.Add($"Duplicate host port mapping for {proto.ToUpperInvariant()} port {rule.HostPort}.");
                continue;
            }

            if (checkHostPortAvailability && proto == "tcp" && !IsLoopbackTcpPortAvailable(rule.HostPort))
            {
                warnings.Add($"Host TCP port {rule.HostPort} ('{rule.Name}') is currently in use on 127.0.0.1.");
            }
        }

        return new PortForwardValidationResult(errors.Count == 0, errors, warnings);
    }

    public static bool IsLoopbackTcpPortAvailable(int port)
    {
        if (port is <= 0 or > 65535)
        {
            return false;
        }

        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
