using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace SBOBForge.Tests;

/// <summary>
/// The committed grammar artifacts must stay byte-identical on every platform
/// (TDS 9): they are embedded into SBOBForge.Parsing and pinned by the
/// grammar-lock sha256, and emitted profiles embed the whole-lock hash
/// (develapp.io/grammar-lock-sha256) — so a line-ending change breaks the
/// frozen lock check and cross-platform drift gates. Found dogfooding the
/// Minotaur choco lane (windows runner: autocrlf CRLF checkout vs the
/// LF-committed bytes). <c>.gitattributes</c> marks them <c>-text</c>;
/// this test fails any PR that reintroduces a carriage return.
/// </summary>
public class GrammarArtifactTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SBOBForge.sln")))
            dir = dir.Parent!;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found (missing SBOBForge.sln)");
    }

    public static IEnumerable<object[]> GrammarArtifacts()
    {
        var dir = Path.Combine(FindRepoRoot(), "src", "SBOBForge.Parsing", "Grammars");
        return Directory.EnumerateFiles(dir, "*.grammar")
            .Select(p => new object[] { Path.GetFileName(p), p });
    }

    [Theory]
    [MemberData(nameof(GrammarArtifacts))]
    public void GrammarArtifactsAreLfOnly(string name, string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.DoesNotContain((byte)'\r', bytes);
    }

    [Theory]
    [MemberData(nameof(GrammarArtifacts))]
    public void EmbeddedGrammarHashMatchesCommittedFile(string name, string path)
    {
        // The embedded resource must be byte-identical to the committed file:
        // same bytes in the working tree, same bytes in the assembly, same
        // sha256 in the frozen lock — on every platform.
        var committed = File.ReadAllBytes(path);
        using var stream = new MemoryStream(committed);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var fileHash = System.BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        var embeddedHash = name == "PowerShell.grammar"
            ? SBOBForge.Parsing.PowerShellSourceParser.GrammarSha256
            : SBOBForge.Parsing.BashSourceParser.GrammarSha256;
        Assert.Equal(fileHash, embeddedHash);
    }
}
