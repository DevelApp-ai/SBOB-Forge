using System;
using System.IO;
using SBOBForge.Cli;
using SBOBForge.Core.Config;
using SBOBForge.Core.Emit;
using Xunit;

namespace SBOBForge.Tests;

/// <summary>
/// Platform byte-identity of emitted text (TDS 9): the string-based
/// System.Text.Json serializer uses <c>Environment.NewLine</c> as its indented
/// line separator — "\r\n" on Windows, "\n" on Unix — so emitters normalize
/// serializer output to LF: committed artifacts are LF on every platform and
/// the drift gate compares bytes (org decision #26).
///
/// Found via the Minotaur choco-pack windows drift gate: the regenerated
/// coverage.json grew by exactly one CR byte per line on windows-latest
/// (1,032,499 → 1,054,855 bytes over 22,350 identical lines) while
/// sbob.yaml and provenance.jsonl — the latter already normalized per row —
/// stayed byte-identical.
/// </summary>
public class CanonicalTextTests
{
    [Fact]
    public void LfOnly_StripsCrLineSeparators()
    {
        Assert.Equal("{\n  \"a\": 1\n}\n", CanonicalText.LfOnly("{\r\n  \"a\": 1\r\n}\r\n"));
    }

    [Fact]
    public void LfOnly_IsIdentityForLfText()
    {
        const string lf = "{\n  \"a\": 1\n}\n";
        Assert.Equal(lf, CanonicalText.LfOnly(lf));
    }

    [Fact]
    public void LfOnly_NeverTouchesEscapedCrSequencesInValues()
    {
        // The JSON encoder escapes control characters inside string values as
        // backslash sequences — normalization may only remove raw line
        // separators, never rewrite serialized content.
        const string withEscapedNewline = "{\"path\":\"a\\r\\nb.txt\"}";
        Assert.Equal(withEscapedNewline, CanonicalText.LfOnly(withEscapedNewline));
    }

    [Fact]
    public void GrammarLockSave_WritesLfOnlyTextAndRoundTripsItsHash()
    {
        var path = Path.Combine(Path.GetTempPath(), "sbobforge-lock-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var lockFile = new GrammarLock();
            lockFile.Grammars["x"] = new GrammarLockEntry { Version = "1.0.0", Sha256 = "deadbeef" };
            lockFile.Minotaur.Version = "1.5.25";
            lockFile.Save(path);
            var written = File.ReadAllText(path);
            Assert.DoesNotContain('\r', written);
            Assert.Equal(lockFile.ComputeLockHash(), GrammarLock.Load(path).ComputeLockHash());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FixturePipelineEmitsLfOnlyArtifacts()
    {
        using var temp = new TempDir(CopyFixtureToTemp());
        var result = GeneratePipeline.Run(new GenerationOptions
        {
            RepoRoot = temp.Path,
            ConfigPath = Path.Combine(temp.Path, "sbobforge.json"),
            LockPath = Path.Combine(temp.Path, "sbobforge.lock.json"),
            GeneratorVersion = Program.Version
        });
        Assert.DoesNotContain('\r', result.SbobYaml);
        Assert.DoesNotContain('\r', result.ProvenanceJsonl);
        Assert.DoesNotContain('\r', result.CoverageJson);
    }

    private static string CopyFixtureToTemp(string fixtureName = "dotnet-xunit-fixture")
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SBOBForge.sln")))
            dir = dir.Parent!;
        var fixture = Path.Combine(dir!.FullName, "examples", fixtureName);
        var temp = Path.Combine(Path.GetTempPath(), "sbobforge-newline-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(fixture, temp);
        return temp;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir(string path) => Path = path;
        public string Path { get; }

        public void Dispose()
        {
            if (System.IO.Path.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
