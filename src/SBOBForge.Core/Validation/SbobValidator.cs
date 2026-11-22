using System;
using System.Collections.Generic;
using System.Linq;

namespace SBOBForge.Core.Validation;

/// <summary>
/// Structural validation equivalent to the SBOB spec v0.0.4 JSON Schema, plus
/// the DevelApp package-purl imageID extension (SBOB-Forge TDS 4; Software-
/// Bill-of-Behavior#33). Line-based over our canonical emitter output; field-
/// path errors in deterministic (document) order.
/// </summary>
public static class SbobValidator
{
    private static readonly HashSet<string> AllowedFlags = new(StringComparer.Ordinal)
    {
        "O_RDONLY", "O_WRONLY", "O_RDWR", "O_CREAT", "O_TRUNC", "O_APPEND",
        "O_CLOEXEC", "O_NONBLOCK", "O_DIRECTORY", "O_NOFOLLOW", "O_PATH"
    };

    private static readonly string[] Architectures = { "amd64", "arm64", "arm", "386", "ppc64le", "s390x", "riscv64" };
    private static readonly string[] Protocols = { "TCP", "UDP", "SCTP" };
    private const string ApiVersion = "spdx.softwarecomposition.kubescape.io/v1beta1";

    /// <summary>Validate the emitted YAML (line-based canonical form produced by our emitter). Returns field-path errors; empty means valid.</summary>
    public static IReadOnlyList<string> Validate(string yaml)
    {
        var errors = new List<string>();
        var lines = yaml.Replace("\r", "").Split('\n');

        if (!ContainsValue(lines, "apiVersion: ", ApiVersion, out _))
            errors.Add("apiVersion: required field missing or wrong value (expected " + ApiVersion + ")");
        if (!ContainsValue(lines, "kind: ", "ContainerProfile", out _))
            errors.Add("kind: required field missing or wrong value (expected ContainerProfile)");
        if (!lines.Any(l => l.Trim().StartsWith("name: ", StringComparison.Ordinal)))
            errors.Add("metadata.name: required field missing");
        if (!lines.Any(l => l.Trim().StartsWith("namespace: ", StringComparison.Ordinal)))
            errors.Add("metadata.namespace: required field missing");
        if (lines.Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("imageID: ", StringComparison.Ordinal)) is not { } imageIdLine)
            errors.Add("spec.imageID: required field missing");
        else
            ValidateImageId(StripPrefix(imageIdLine, "imageID: "), errors);

        // Single ordered pass with section tracking so path/flag errors carry correct field paths.
        var section = "";
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            if (line.StartsWith("  ", StringComparison.Ordinal) && !line.StartsWith("    ", StringComparison.Ordinal)
                && trimmed.EndsWith(":", StringComparison.Ordinal))
            {
                var head = trimmed.TrimEnd(':');
                if (head is "execs" or "opens" or "ingress" or "egress" or "capabilities" or "endpoints" or "rulePolicies")
                    section = head;
                continue;
            }

            if (trimmed.StartsWith("- path: ", StringComparison.Ordinal) || trimmed.StartsWith("path: ", StringComparison.Ordinal))
            {
                var field = section switch { "execs" => "execs.path", "opens" => "opens.path", _ => section + ".path" };
                ValidatePath(StripPrefix(trimmed, "- path: "), errors, field);
                continue;
            }

            if (trimmed.StartsWith("flags: [", StringComparison.Ordinal))
            {
                foreach (var f in SplitFlow(trimmed, "flags: ["))
                    if (!AllowedFlags.Contains(f))
                        errors.Add($"{section}.flags: '{f}' is not a legal open(2) flag token");
                continue;
            }

            if (trimmed.StartsWith("args: [", StringComparison.Ordinal))
            {
                foreach (var a in SplitFlow(trimmed, "args: ["))
                    if (a.Contains("...", StringComparison.Ordinal))
                        errors.Add($"{section}.args: '{a}': three-dot run is not the spec 5 wildcard; use U+22EF ('⋯')");
                continue;
            }

            // dnsNames wildcard tokens (spec 5.8; SBOB-Forge #6 negative corpus):
            // leading *.<suffix>, mid <a>.⋯.<b>, or trailing <prefix>.*.
            // '**' is a recursive glob and MUST be rejected; a mid-label '*' is
            // non-standard (bind/coredns reject it) and must be written '⋯';
            // '⋯' must occupy one whole dot-label with labels on both sides.
            if (trimmed.StartsWith("dnsNames: [", StringComparison.Ordinal)
                && (section is "ingress" or "egress"))
            {
                foreach (var d in SplitFlow(trimmed, "dnsNames: ["))
                    ValidateDnsName(d, errors, section);
                continue;
            }

            if (trimmed.StartsWith("architectures: [", StringComparison.Ordinal))
            {
                foreach (var a in SplitFlow(trimmed, "architectures: ["))
                    if (!Architectures.Contains(a))
                        errors.Add($"spec.architectures: unknown Linux architecture token '{a}'");
                continue;
            }

            if (trimmed.StartsWith("- { port: ", StringComparison.Ordinal))
            {
                var body = trimmed["- { port: ".Length..];
                var portStr = body.Split(',')[0].Trim();
                if (!int.TryParse(portStr, out var port) || port is < 1 or > 65535)
                    errors.Add($"ports.port: '{portStr}' is not a valid port");
                var proto = body.Split(',').Length > 1
                    ? body.Split(',')[1].Replace("protocol: ", "").Trim().TrimEnd('}').Trim('"').Trim()
                    : "";
                if (!Protocols.Contains(proto))
                    errors.Add($"ports.protocol: '{proto}' is not a valid protocol token (TCP/UDP/SCTP)");
            }
        }
        return errors;
    }

    private static bool ContainsValue(string[] lines, string prefix, string expected, out string found)
    {
        foreach (var l in lines)
        {
            var t = l.Trim();
            if (!t.StartsWith(prefix, StringComparison.Ordinal)) continue;
            found = Unquote(t[prefix.Length..].Trim());
            if (found == expected) return true;
        }
        found = "";
        return false;
    }

    /// <summary>imageID: OCI reference (spec 4.1) or package purl (TDS 4 extension).</summary>
    private static void ValidateImageId(string value, List<string> errors)
    {
        var v = Unquote(value);
        if (v.StartsWith("pkg:", StringComparison.Ordinal))
        {
            var body = v[4..];
            var slash = body.IndexOf('/');
            var at = body.LastIndexOf('@');
            if (slash <= 0 || at <= slash + 1 || at == body.Length - 1)
                errors.Add($"spec.imageID: malformed package purl '{v}'");
            return;
        }
        if (!v.Contains(':') && !v.Contains('@'))
            errors.Add($"spec.imageID: '{v}' is neither an OCI image reference nor a pkg: purl");
    }

    /// <summary>Path wildcard legality (spec 5, TDS 8.1 self-lint): '⋯' must occupy a whole segment; '...' is never legal.</summary>
    private static void ValidatePath(string value, List<string> errors, string field)
    {
        if (value.Contains("...", StringComparison.Ordinal))
        {
            errors.Add($"{field}: '{value}': three-dot run is not the spec 5 wildcard; use U+22EF ('⋯')");
            return;
        }
        if (!value.Contains('\u22EF')) return;
        foreach (var seg in value.Split('/'))
            if (seg.Contains('\u22EF') && seg != "\u22EF")
                errors.Add($"{field}: '{value}': '⋯' must occupy a whole path segment");
    }

    /// <summary>
    /// dnsNames token legality (spec 5.8): '**' MUST be rejected; a '*' may only
    /// lead (*.suffix, exactly one label) or trail (prefix.*); a mid-label '*' is
    /// non-standard and must be written '⋯'; and the mid '⋯' form must be
    /// &lt;a&gt;.⋯.&lt;b&gt; — one whole dot-label with at least one label on each side.
    /// </summary>
    private static void ValidateDnsName(string d, List<string> errors, string section)
    {
        var err = section + ".dnsNames";
        if (d.Contains("...", StringComparison.Ordinal))
        {
            errors.Add($"{err}: '{d}': three-dot run is not the spec 5 wildcard; use U+22EF ('⋯')");
            return;
        }
        if (d.Contains("**", StringComparison.Ordinal))
        {
            errors.Add($"{err}: '{d}': '**' (recursive glob) is rejected; use leading *.<suffix>, mid <a>.⋯.<b>, or trailing <prefix>.*");
            return;
        }
        var labels = d.Split('.');
        if (d.Contains('*'))
        {
            // Legal positions: bare leading label (index 0) or final label; trailing
            // dot form may leave an empty last label after the '*'.
            var starIdx = Array.FindIndex(labels, l => l.Contains('*'));
            if (labels[starIdx] != "*")
                errors.Add($"{err}: '{d}': '*' must occupy a whole dot-label");
            else if (starIdx != 0 && !(starIdx == labels.Length - 1
                     || (starIdx == labels.Length - 2 && labels[^1].Length == 0)))
                errors.Add($"{err}: '{d}': mid-label '*' is non-standard (spec 5.8); use '⋯' for the mid position");
            return;
        }
        if (d.Contains('\u22EF'))
        {
            var starIdx = Array.FindIndex(labels, l => l.Contains('\u22EF'));
            var trailingDot = labels[^1].Length == 0;
            var lastLabel = trailingDot ? labels.Length - 2 : labels.Length - 1;
            var legal = labels[starIdx] == "\u22EF"
                && starIdx >= 1 // at least one label before (<a>.)
                && lastLabel > starIdx; // at least one label after (.<b>)
            if (!legal)
                errors.Add($"{err}: '{d}': mid form must be <a>.⋯.<b> — one whole dot-label with labels on both sides");
        }
    }

    private static string StripPrefix(string s, string prefix) =>
        Unquote(s[(s.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length)..].Trim());

    private static IEnumerable<string> SplitFlow(string line, string prefix)
    {
        var inner = line[prefix.Length..].TrimEnd(']', ' ');
        if (inner.Length == 0) yield break;
        foreach (var item in inner.Split(','))
            yield return Unquote(item.Trim());
    }

    private static string Unquote(string v) =>
        v.Length >= 2 && v.StartsWith('"') && v.EndsWith('"') ? v[1..^1] : v;
}
