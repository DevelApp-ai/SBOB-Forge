using System;
using System.Text;

namespace SBOBForge.Core.Emit;

/// <summary>
/// Dialect bridge (TDS 10.4): deterministic conversion of a canonically emitted
/// kubescape-dialect profile (spdx.softwarecomposition.kubescape.io/v1beta1 /
/// ContainerProfile) into the Software-Bill-of-Behavior registry dialect
/// (sbob.k8sstormcenter.io/v1 / SoftwareBillOfBehavior) consumed by the
/// sbobs/ registry and the sbobctl transpiler/validation lane.
///
/// The two dialects are isomorphic below the header: execs, opens, ingress,
/// egress, architectures and the develapp.io/* annotations all pass the
/// registry validator unchanged, so the conversion is a guarded header-level
/// rewrite. Everything below the header stays byte-identical — the
/// develapp.io/behavior-hash annotation still binds the converted file.
/// The kubescape UI marker annotation (kubescape.io/managed-by: User) has no
/// meaning in the registry dialect and is swapped for the registry's
/// sbob.spec-version marker (spec v0.0.4, optional informational metadata
/// for the transpiler IR).
/// </summary>
public static class DialectConverter
{
    public const string KubescapeApiVersion = "spdx.softwarecomposition.kubescape.io/v1beta1";
    public const string KubescapeKind = "ContainerProfile";
    public const string SbobApiVersion = "sbob.k8sstormcenter.io/v1";
    public const string SbobKind = "SoftwareBillOfBehavior";

    private const string HeaderLine = "apiVersion: " + KubescapeApiVersion + "\n";
    private const string KindLine = "kind: " + KubescapeKind + "\n";
    private const string ManagedByLine = "    kubescape.io/managed-by: User\n";
    private const string SpecVersionLine = "    sbob.spec-version: \"0.0.4\"\n";

    /// <summary>
    /// Convert a canonical kubescape-dialect profile into the registry dialect.
    /// Throws <see cref="ParseHardError"/> (exit 2) when the input is not a
    /// canonically emitted kubescape-dialect profile — a foreign file could
    /// hide behavior and must never be silently header-swapped.
    /// </summary>
    public static string ToSbobDialect(string canonical)
    {
        if (string.IsNullOrEmpty(canonical))
            throw new ParseHardError("convert: input is empty");

        if (!canonical.StartsWith(HeaderLine, StringComparison.Ordinal))
            throw new ParseHardError(
                $"convert: not a canonically emitted kubescape-dialect profile: expected first line \"apiVersion: {KubescapeApiVersion}\"");

        var kindLineEnd = canonical.IndexOf('\n', HeaderLine.Length);
        if (kindLineEnd < 0)
            throw new ParseHardError("convert: truncated profile: no kind line");
        kindLineEnd += 1;
        var kindLine = canonical[HeaderLine.Length..kindLineEnd];
        if (kindLine != KindLine)
            throw new ParseHardError(
                $"convert: not a canonically emitted kubescape-dialect profile: expected second line \"{KindLine.TrimEnd()}\", got \"{kindLine.TrimEnd()}\"");

        var managedByIndex = canonical.IndexOf(ManagedByLine, kindLineEnd, StringComparison.Ordinal);
        if (managedByIndex < 0)
            throw new ParseHardError(
                "convert: expected the canonical annotation block to carry \"kubescape.io/managed-by: User\"");

        var sb = new StringBuilder(canonical.Length + 16);
        sb.Append("apiVersion: ").Append(SbobApiVersion).Append('\n');
        sb.Append("kind: ").Append(SbobKind).Append('\n');
        sb.Append(canonical[kindLineEnd..managedByIndex]);
        sb.Append(SpecVersionLine);
        sb.Append(canonical[(managedByIndex + ManagedByLine.Length)..]);
        return sb.ToString();
    }
}
