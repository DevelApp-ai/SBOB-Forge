using System;
using System.IO;
using System.Linq;
using SBOBForge.Core.Discovery;
using Xunit;

namespace SBOBForge.Tests;

public class DiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sbobforge-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void EnumeratesAndSortsOrdinal()
    {
        Create("src/Zeta/Program.cs", "src/apple/Routes.cs", "src/Beta/b.cs", "src/Beta/a.cs", "src/skip.txt");
        var found = FileDiscovery.Enumerate(_root, new[] { "src/**/*.cs" });
        Assert.Equal(new[]
        {
            // Ordinal: uppercase letters sort before lowercase (Zeta < apple, Beta < apple)
            "src/Beta/a.cs", "src/Beta/b.cs", "src/Zeta/Program.cs", "src/apple/Routes.cs"
        }, found);
    }

    [Fact]
    public void StarGlobStaysWithinItsSegment()
    {
        Create("src/a.cs", "src/deep/b.cs");
        var found = FileDiscovery.Enumerate(_root, new[] { "src/*.cs" });
        Assert.Equal(new[] { "src/a.cs" }, found);
    }

    [Fact]
    public void DuplicateGlobsDeduplicate()
    {
        Create("src/a.cs");
        var found = FileDiscovery.Enumerate(_root, new[] { "src/**/*.cs", "src/a.cs", "src/*.cs" });
        Assert.Equal(new[] { "src/a.cs" }, found);
    }

    [Fact]
    public void MissingDirectoriesYieldNothing()
    {
        Assert.Empty(FileDiscovery.Enumerate(_root, new[] { "nope/**/*.cs" }));
    }

    private void Create(params string[] relative)
    {
        foreach (var rel in relative)
        {
            var full = Path.Combine(_root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "// fixture");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
