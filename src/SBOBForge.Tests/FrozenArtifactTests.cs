using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Xunit;

namespace SBOBForge.Tests;

/// <summary>
/// Artifacts embedded into the assemblies and pinned by the frozen grammar
/// lock (TDS 5.3/5.4, TDS 9 determinism): the authored grammar artifacts
/// (<c>SBOBForge.Parsing/Grammars/*.grammar</c>) and the language catalogs
/// (<c>SBOBForge.Catalog/catalog/*.json</c>) must be byte-identical on every
/// platform. A line-ending change (git autocrlf on Windows checkouts) breaks
/// the frozen sha256 pins and — because emitted profiles embed the
/// whole-lock hash (<c>develapp.io/grammar-lock-sha256</c>) — breaks
/// cross-platform drift gates. Found dogfooding the Minotaur choco lane
/// (windows runner: CRLF checkout vs LF-committed bytes — first the
/// powershell grammar, then the dotnet catalog). <c>.gitattributes</c> marks
/// these paths <c>-text</c>; these tests fail any PR that reintroduces a
/// carriage return or an embed/checkout divergence.
/// </summary>
public class FrozenArtifactTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SBOBForge.sln")))
            dir = dir.Parent!;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found (missing SBOBForge.sln)");
    }

    private static string Sha256Hex(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return System.Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
    }

    public static IEnumerable<object[]> GrammarArtifacts()
    {
        var dir = Path.Combine(FindRepoRoot(), "src", "SBOBForge.Parsing", "Grammars");
        return Directory.EnumerateFiles(dir, "*.grammar").Select(p => new object[] { p });
    }

    public static IEnumerable<object[]> CatalogArtifacts()
    {
        var dir = Path.Combine(FindRepoRoot(), "src", "SBOBForge.Catalog", "catalog");
        return Directory.EnumerateFiles(dir, "*.json").Select(p => new object[] { p });
    }

    [Theory]
    [MemberData(nameof(GrammarArtifacts))]
    public void GrammarArtifactsAreLfOnly(string path)
    {
        Assert.DoesNotContain((byte)'\r', File.ReadAllBytes(path));
    }

    [Theory]
    [MemberData(nameof(CatalogArtifacts))]
    public void CatalogArtifactsAreLfOnly(string path)
    {
        Assert.DoesNotContain((byte)'\r', File.ReadAllBytes(path));
    }

    [Theory]
    [MemberData(nameof(GrammarArtifacts))]
    public void EmbeddedGrammarHashMatchesCommittedFile(string path)
    {
        // The embedded resource must be byte-identical to the committed file:
        // same bytes in the working tree, same bytes in the assembly, same
        // sha256 in the frozen lock — on every platform.
        var fileHash = Sha256Hex(File.ReadAllBytes(path));
        var embeddedHash = Path.GetFileName(path) == "PowerShell.grammar"
            ? SBOBForge.Parsing.PowerShellSourceParser.GrammarSha256
            : SBOBForge.Parsing.BashSourceParser.GrammarSha256;
        Assert.Equal(fileHash, embeddedHash);
    }

    [Theory]
    [MemberData(nameof(CatalogArtifacts))]
    public void EmbeddedCatalogHashMatchesCommittedFile(string path)
    {
        // The manifest LogicalName is "SBOBForge.Catalog.<file>" (csproj):
        // the embedded catalog resource must hash to the same sha256 the
        // frozen lock pins for it, and to the committed working-tree file.
        var manifestName = "SBOBForge.Catalog." + Path.GetFileName(path);
        Assert.Equal(Sha256Hex(File.ReadAllBytes(path)), SBOBForge.Catalog.LanguageCatalog.ComputeHash(manifestName));
    }
}
