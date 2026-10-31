using System.Linq;
using SBOBForge.Adapters.DotNet;
using SBOBForge.Parsing;
using Xunit;

namespace SBOBForge.Tests;

public class AdapterTests
{
    private static CodeModel Parse(string source) =>
        new RoslynCSharpParser().Parse("tests/Sample.cs", source);

    [Fact]
    public void XunitFactsAndTheories()
    {
        var model = Parse("""
            using Xunit;
            namespace Ns;

            public sealed class T
            {
                [Fact]
                public void Simple() { }

                [Theory]
                [InlineData("/a.log")]
                [InlineData("/b.log")]
                public void Theory(string path) { }
            }
            """);
        var registry = new TestFrameworkRegistry();
        Assert.Equal("xunit", Assert.Single(registry.Detect(model)).FrameworkId);

        var entries = registry.ExtractEntries(model);
        Assert.Equal(3, entries.Count);
        Assert.Equal("Ns.T.Simple", entries[0].TestId);
        Assert.True(entries[0].Enabled);
        Assert.Equal("Ns.T.Theory[0]", entries[1].TestId);
        Assert.Equal("/a.log", Assert.Single(entries[1].TheoryCaseValues));
        Assert.Equal("Ns.T.Theory[1]", entries[2].TestId);
        Assert.Equal("/b.log", Assert.Single(entries[2].TheoryCaseValues));
    }

    [Fact]
    public void XunitSkipDisablesTheEntry()
    {
        var model = Parse("""
            public sealed class T
            {
                [Fact(Skip = "flaky")]
                public void Skipped() { }
            }
            """);
        var entry = Assert.Single(new XUnitAdapter().ExtractEntries(model));
        Assert.False(entry.Enabled);
    }

    [Fact]
    public void NUnitTestAndIgnore()
    {
        var model = Parse("""
            public sealed class T
            {
                [Test]
                public void Ok() { }

                [Test(Ignore = "broken")]
                public void Ignored() { }

                [TestCase("/x")]
                public void Case(string p) { }
            }
            """);
        var registry = new TestFrameworkRegistry();
        Assert.Equal("nunit", Assert.Single(registry.Detect(model)).FrameworkId);
        var entries = registry.ExtractEntries(model);
        Assert.Equal(3, entries.Count);
        Assert.True(entries[0].Enabled);
        Assert.False(entries[1].Enabled);
        Assert.Equal("/x", Assert.Single(entries[2].TheoryCaseValues));
    }

    [Fact]
    public void MSTestTestMethodAndDataRows()
    {
        var model = Parse("""
            public sealed class T
            {
                [TestMethod]
                public void Ok() { }

                [DataTestMethod]
                [DataRow("/a")]
                [DataRow("/b")]
                public void Rows(string p) { }

                [TestMethod]
                [Ignore]
                public void Ignored() { }
            }
            """);
        var registry = new TestFrameworkRegistry();
        Assert.Equal("mstest", Assert.Single(registry.Detect(model)).FrameworkId);
        var entries = registry.ExtractEntries(model);
        Assert.Equal(4, entries.Count);
        Assert.True(entries[0].Enabled);
        Assert.Equal("T.Rows[0]", entries[1].TestId);
        Assert.Equal("T.Rows[1]", entries[2].TestId);
        Assert.False(entries[3].Enabled);
    }

    [Fact]
    public void RegistryReportsNothingForPlainCode()
    {
        var model = Parse("""
            public sealed class T
            {
                public void M() { }
            }
            """);
        var registry = new TestFrameworkRegistry();
        Assert.Empty(registry.Detect(model));
        Assert.Empty(registry.ExtractEntries(model));
    }
}
