using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SBOBForge.Core.Model;

namespace SBOBForge.Core.Emit;

/// <summary>
/// Canonical SBOB emitter (TDS 8). Hand-rolled YAML writer: fixed section and
/// field order, LF endings, 2-space indent, flow style for scalar lists and
/// block style for object entries, no anchors/aliases/tags. Absent sections
/// (NULL semantics, spec 5.4) are omitted; explicit-empty (NONE) is never
/// auto-generated. '⋯' is always the single codepoint U+22EF.
/// </summary>
public static class CanonicalSbobEmitter
{
    /// <summary>Spec 5.4 wildcard: single codepoint U+22EF. Never three dots.</summary>
    public const string SegmentWildcard = "\u22EF";
    public const string ExecArgsWildcard = "\u22EF\u22EF";

    /// <summary>Emit the full ContainerProfile YAML document for the IR.</summary>
    public static string Emit(BehaviorIr ir, string generatorVersion, string metadataNamespace)
    {
        if (ir.Execs.Count == 0 && ir.Opens.Count == 0 && ir.Egress.Count == 0 && ir.Ingress.Count == 0
            && ir.Coverage.Quarantined.Count > 0)
        {
            throw new EmitException(
                "Section-shadowed profile: every candidate entry is quarantined. " +
                "Refusing to emit an empty profile silently — inspect the coverage report (TDS 8.4).");
        }

        var sb = new StringBuilder();
        sb.Append("apiVersion: spdx.softwarecomposition.kubescape.io/v1beta1\n");
        sb.Append("kind: ContainerProfile\n");
        sb.Append("metadata:\n");
        sb.Append("  name: ").Append(Scalar(ir.Package.MetadataName)).Append('\n');
        sb.Append("  namespace: ").Append(Scalar(metadataNamespace)).Append('\n');
        sb.Append("  annotations:\n");
        sb.Append("    kubescape.io/managed-by: User\n");
        sb.Append("    develapp.io/binding: package-purl\n");
        sb.Append("    develapp.io/generator: sbobforge@").Append(Scalar(generatorVersion)).Append('\n');
        sb.Append("    develapp.io/grammar-lock-sha256: ").Append(ir.GrammarLockHash).Append('\n');
        sb.Append("    develapp.io/behavior-hash: ").Append(ir.ComputeBehaviorHash()).Append('\n');
        // TDS 5.4 Phase A: files analyzed by fixed lexical scanners carry a reduced
        // confidence tier; the SBOB annotation records it so consumers know.
        if (ir.Coverage.LexicalParserList.Count > 0)
            sb.Append("    develapp.io/parser: lexical\n");
        sb.Append("spec:\n");
        if (ir.Architectures.Count > 0)
            sb.Append("  architectures: [").Append(string.Join(", ", ir.Architectures.Select(Scalar))).Append("]\n");
        sb.Append("  imageID: ").Append(Scalar(ir.Package.ImageId)).Append('\n');

        // Fixed section order (TDS 8.1): capabilities, endpoints, execs, opens, ingress, egress, rulePolicies.
        // capabilities/endpoints/rulePolicies are not test-derivable in IR v1 → NULL → omitted (TDS 8.4).
        if (ir.Execs.Count > 0)
        {
            sb.Append("  execs:\n");
            foreach (var e in ir.Execs)
            {
                sb.Append("    - path: ").Append(Scalar(e.Path)).Append('\n');
                var args = e.Args.Count == 0
                    ? new List<string> { e.Path, ExecArgsWildcard }
                    : e.Args;
                sb.Append("      args: [").Append(string.Join(", ", args.Select(Scalar))).Append("]\n");
            }
        }
        if (ir.Opens.Count > 0)
        {
            sb.Append("  opens:\n");
            foreach (var o in ir.Opens)
            {
                sb.Append("    - path: ").Append(Scalar(o.Path)).Append('\n');
                sb.Append("      flags: [").Append(string.Join(", ", o.Flags.Select(Scalar))).Append("]\n");
            }
        }
        if (ir.Ingress.Count > 0) EmitNet(sb, "ingress", ir.Ingress);
        if (ir.Egress.Count > 0) EmitNet(sb, "egress", ir.Egress);

        return sb.ToString();
    }

    private static void EmitNet(StringBuilder sb, string section, IReadOnlyList<NetEvent> events)
    {
        sb.Append("  ").Append(section).Append(":\n");
        foreach (var n in events)
        {
            sb.Append("    - identifier: ").Append(Scalar(NetIdentifier(n))).Append('\n');
            sb.Append("      type: ").Append("external").Append('\n');
            if (n.DnsName.Length > 0)
                sb.Append("      dnsNames: [").Append(Scalar(n.DnsName)).Append("]\n");
            if (n.IpAddresses.Count > 0)
                sb.Append("      ipAddresses: [").Append(string.Join(", ", n.IpAddresses.Select(Scalar))).Append("]\n");
            if (n.Port is not null)
                sb.Append("      ports:\n        - { port: ").Append(n.Port).Append(", protocol: ")
                  .Append(Scalar(n.Protocol)).Append(" }\n");
        }
    }

    private static string NetIdentifier(NetEvent n) =>
        (n.DnsName.Length > 0
                ? n.DnsName
                : n.IpAddresses.Count > 0 ? string.Join("-", n.IpAddresses) : $"port-{n.Port ?? 0}")
        + "-" + n.Direction.ToString().ToLowerInvariant();

    /// <summary>YAML scalar rendering: double quotes only when needed (TDS 8.1: minimal, deterministic).</summary>
    public static string Scalar(string value)
    {
        if (value.Length == 0) return "\"\"";
        if (NeedsQuotes(value)) return Quote(value);
        return value;
    }

    /// <summary>True when the value needs double quotes to survive YAML plain-scalar rules.</summary>
    private static bool NeedsQuotes(string v) =>
        v.Length == 0
        || char.IsWhiteSpace(v[0]) || char.IsWhiteSpace(v[^1])
        || v.Contains(": ") || v.EndsWith(':')
        || v.Contains('#')
        || v.Contains("⋯") || v.Contains('*')
        || v.Contains('\\')
        || v[0] is '?' or '-' or '!' or '&' or ',' or '[' or ']' or '{' or '}'
           or '|' or '>' or '\'' or '"' or '%' or '@' or '`';

    private static string Quote(string v)
    {
        var sb = new StringBuilder(v.Length + 2);
        sb.Append('"');
        foreach (var c in v)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}

/// <summary>Emitter self-lint failure (TDS 8.1): refuse three-dot wildcard runs and other malformed values.</summary>
public sealed class EmitException(string message) : Exception(message);
