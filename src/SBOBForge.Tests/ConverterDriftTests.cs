using System;
using System.IO;
using System.Linq;
using SBOBForge.Core;
using SBOBForge.Core.Emit;
using Xunit;

namespace SBOBForge.Tests;

/// <summary>
/// Dialect-drift tests (SBOB-Forge #29, closed by DevelApp-Repo-Global decision
/// "registry dialect is primary; Kubescape maintained for compatibility"): the
/// registry dialect (sbob.k8sstormcenter.io/v1) is the ecosystem's main dialect
/// and the Kubescape dialect stays as the compatibility surface, mediated by the
/// permanent guarded bridge (TDS 10.4). These tests pin the bridge's contract so
/// the two dialects cannot silently diverge: for every committed golden, convert
/// must be exactly a header+marker rewrite — payload bytes identical below the
/// three changed lines, deterministic, and fail-closed on non-canonical input.
/// </summary>
public class ConverterDriftTests
{
    private const string KubescapeHeader =
        "apiVersion: " + DialectConverter.KubescapeApiVersion + "\nkind: " + DialectConverter.KubescapeKind + "\n";
    private const string RegistryHeader =
        "apiVersion: " + DialectConverter.SbobApiVersion + "\nkind: " + DialectConverter.SbobKind + "\n";
    private const string ManagedByLine = "    kubescape.io/managed-by: User\n";
    private const string SpecVersionLine = "    sbob.spec-version: \"0.0.4\"\n";

    private static string[] GoldenFiles()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SBOBForge.sln")))
            dir = dir.Parent!;
        Assert.NotNull(dir);
        var goldens = Directory.EnumerateFiles(
            Path.Combine(dir!.FullName, "examples"), "*.sbob.yaml", SearchOption.AllDirectories).ToList();
        Assert.True(goldens.Count >= 13, $"expected the 13-fixture corpus, found {goldens.Count}");
        return goldens.ToArray();
    }

    [Fact]
    public void EveryGoldenConvertsAsExactlyAHeaderAndMarkerRewrite()
    {
        foreach (var golden in GoldenFiles())
        {
            var canonical = File.ReadAllText(golden);
            Assert.StartsWith(KubescapeHeader, canonical, StringComparison.Ordinal);
            Assert.Equal(1, CountOccurrences(canonical, ManagedByLine));

            var converted = DialectConverter.ToSbobDialect(canonical);

            // the exact expected bytes: registry header + canonical payload with the
            // marker line swapped — nothing else may change, so any drift in either
            // dialect's spelling, ordering, or converter behavior fails right here
            var expected = RegistryHeader
                + canonical[KubescapeHeader.Length..].Replace(ManagedByLine, SpecVersionLine);
            Assert.Equal(expected, converted);
        }
    }

    [Fact]
    public void ConvertedProfilesCarryRegistryMarkersAndNeverKubescapeOnes()
    {
        foreach (var golden in GoldenFiles())
        {
            var canonical = File.ReadAllText(golden);
            var converted = DialectConverter.ToSbobDialect(canonical);

            Assert.StartsWith(RegistryHeader, converted, StringComparison.Ordinal);
            Assert.Contains(SpecVersionLine, converted, StringComparison.Ordinal);
            Assert.DoesNotContain(DialectConverter.KubescapeApiVersion, converted, StringComparison.Ordinal);
            Assert.Contains("kubescape.io/managed-by", canonical, StringComparison.Ordinal);
            Assert.DoesNotContain("kubescape.io/managed-by", converted, StringComparison.Ordinal);

            // the binding hash rides the payload unchanged — it must still pin the
            // registry-dialect copy (TDS 8.3, 10.4)
            var hash = canonical.Split('\n').Single(l => l.Contains("develapp.io/behavior-hash"));
            Assert.Contains(hash, converted, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ConversionIsDeterministic()
    {
        foreach (var golden in GoldenFiles())
        {
            var canonical = File.ReadAllText(golden);
            Assert.Equal(
                DialectConverter.ToSbobDialect(canonical),
                DialectConverter.ToSbobDialect(canonical));
        }
    }

    [Fact]
    public void BridgeRefusesNonCanonicalInput()
    {
        // already-registry input must not be silently re-converted (idempotence is
        // not the contract — a foreign or already-converted file is rejected, TDS 10.4)
        var canonical = File.ReadAllText(GoldenFiles()[0]);
        var converted = DialectConverter.ToSbobDialect(canonical);
        Assert.Throws<ParseHardError>(() => DialectConverter.ToSbobDialect(converted));

        Assert.Throws<ParseHardError>(() => DialectConverter.ToSbobDialect(""));
        Assert.Throws<ParseHardError>(() => DialectConverter.ToSbobDialect(
            "apiVersion: v1\nkind: Pod\nmetadata:\n  name: x\n"));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
