using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace SBOBForge.Core.Validation;

/// <summary>
/// Public-lane content gate (TDS 10.5): a profile published to a PUBLIC registry
/// lane must not carry internal infrastructure disclosure — private DNS suffixes,
/// internal (non-loopback) IP ranges, or bare single-label hostnames lifted from
/// test-suite literals. The gate is a mechanical pipeline control, not review
/// discipline: it fails BEFORE the registry PR is opened (org decision #26 spirit —
/// a gate that runs after publication is documentation).
///
/// Scope: the network targets of the profile — every `dnsNames:` / `ipAddresses:`
/// entry under `ingress:` / `egress:`. Exec paths and open paths are local to the
/// installed package and are not infrastructure disclosure.
///
/// Deterministic, line-based over our canonical emitter output (same contract as
/// SbobValidator); violations are reported in document order. Works on either
/// dialect — the payload below the header is byte-identical across the bridge.
///
/// Loopback (`localhost`, 127.0.0.0/8, ::1) is ALLOWED by default: every test
/// suite dials loopback fixtures, loopback reveals no estate topology, and
/// forbidding it would make the gate noise. Everything else private fails.
/// </summary>
public static class PublicLaneGate
{
    /// <summary>Private DNS suffixes (lowercase, leading dot form) that never belong on a public lane.</summary>
    private static readonly string[] PrivateSuffixes =
    {
        ".internal", ".intranet", ".corp", ".lan", ".local", ".private", ".home", ".localdomain"
    };

    /// <summary>Verdict for one network target. Names the rule that fired so the failure is actionable.</summary>
    public sealed record Violation(string Section, string Field, string Value, string Rule);

    /// <summary>
    /// Check a canonical (or registry-dialect) profile YAML for public-lane disclosure.
    /// Returns violations in document order; empty means publishable on the public lane.
    /// <paramref name="allowed"/> entries are exact-value or suffix exemptions (e.g. "db",
    /// "10.0.0.5", ".dev.local") — an override is a reviewed decision, reviewable in the diff.
    /// </summary>
    public static IReadOnlyList<Violation> Check(string yaml, IEnumerable<string>? allowed = null)
    {
        var allow = new HashSet<string>((allowed ?? Array.Empty<string>()).Select(a => a.Trim().ToLowerInvariant()), StringComparer.Ordinal);
        var violations = new List<Violation>();
        var lines = yaml.Replace("\r", "").Split('\n');

        var section = ""; // "ingress" | "egress" | anything else (no gate outside net sections)
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed == "ingress:" || trimmed == "egress:")
            {
                section = trimmed[..^1];
                continue;
            }
            // A new top-level key ends the section (column-0 "key:").
            if (lines[i].Length > 0 && !char.IsWhiteSpace(lines[i][0]) && trimmed.EndsWith(':'))
            {
                section = "";
                continue;
            }

            if (section.Length == 0) continue;
            if (trimmed.StartsWith("dnsNames:", StringComparison.Ordinal))
                CheckList(violations, allow, section, "dnsNames", trimmed["dnsNames:".Length..]);
            else if (trimmed.StartsWith("ipAddresses:", StringComparison.Ordinal))
                CheckList(violations, allow, section, "ipAddresses", trimmed["ipAddresses:".Length..]);
        }
        return violations;
    }

    private static void CheckList(List<Violation> violations, HashSet<string> allow, string section, string field, string bracketContent)
    {
        foreach (var raw in SplitScalars(bracketContent))
        {
            var value = Unquote(raw);
            if (value.Length == 0) continue;
            var rule = RuleFor(value);
            if (rule is null) continue;
            if (IsAllowed(allow, value)) continue;
            violations.Add(new Violation(section, field, value, rule));
        }
    }

    /// <summary>The disclosure rule the value trips, or null when the value is public-lane safe.</summary>
    private static string? RuleFor(string value)
    {
        if (IPAddress.TryParse(value, out var ip))
        {
            if (ip.IsIPv6LinkLocal) return "link-local address";
            if (ip.IsIPv6SiteLocal) return "site-local address";
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                // Unique-local fc00::/7 (fc/fd prefix) — the private IPv6 range.
                if ((ip.GetAddressBytes()[0] & 0xFE) == 0xFC) return "unique-local IPv6 address (fc00::/7)";
                if (IPAddress.IsLoopback(ip)) return null; // ::1 — loopback, allowed
                return null;
            }
            if (IPAddress.IsLoopback(ip)) return null; // 127.0.0.0/8 — loopback, allowed
            var b = ip.GetAddressBytes();
            if (b[0] == 10) return "RFC1918 private address (10.0.0.0/8)";
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return "RFC1918 private address (172.16.0.0/12)";
            if (b[0] == 192 && b[1] == 168) return "RFC1918 private address (192.168.0.0/16)";
            if (b[0] == 169 && b[1] == 254) return "link-local address (169.254.0.0/16)";
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return "shared address space (100.64.0.0/10, CGNAT)";
            return null; // public IP literal
        }

        // DNS name rules.
        var host = value.ToLowerInvariant();
        if (host == "localhost") return null;
        if (host.IndexOf('.') < 0) return "bare single-label hostname (internal service name)";
        foreach (var suffix in PrivateSuffixes)
            if (host.EndsWith(suffix, StringComparison.Ordinal))
                return $"private DNS suffix ({suffix})";
        return null; // ordinary FQDN
    }

    private static bool IsAllowed(HashSet<string> allow, string value)
    {
        var host = value.ToLowerInvariant();
        if (allow.Contains(host)) return true;
        foreach (var a in allow)
            if (a.StartsWith('.') && host.EndsWith(a, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Split a flow-sequence body "[a, b, c]" into scalars.</summary>
    private static IEnumerable<string> SplitScalars(string bracketContent)
    {
        var body = bracketContent.Trim();
        if (body.StartsWith('[')) body = body[1..];
        if (body.EndsWith(']')) body = body[..^1];
        if (body.Length == 0) yield break;
        foreach (var part in body.Split(','))
            yield return part.Trim();
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') return s[1..^1];
        return s;
    }
}
