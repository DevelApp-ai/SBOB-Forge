using System.Linq;
using SBOBForge.Adapters.PowerShell;
using SBOBForge.Parsing;
using Xunit;

namespace SBOBForge.Tests;

public class PesterAdapterTests
{
    private static CodeModel Parse(string source) =>
        new PowerShellSourceParser().Parse("tests/ps/config-store.tests.ps1", source);

    [Fact]
    public void ItBlocksBecomeEntriesWithFullDescribePaths()
    {
        var model = Parse("""
            Describe "config store" {
                It "loads config" {
                    Load-Config
                }

                It "saves cache" {
                    Save-Cache "payload"
                }
            }
            """);
        var adapter = new PesterAdapter();
        Assert.True(adapter.CanAdapt(model));
        Assert.Equal("pester", adapter.FrameworkId);

        var entries = adapter.ExtractEntries(model);
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.True(e.Enabled));
        Assert.Contains(entries, e =>
            e.TestId == "tests/ps/config-store.tests.ps1::config store > loads config");
        Assert.Contains(entries, e => e.EntryMethod.EndsWith("@2"));
    }

    [Fact]
    public void SkipParameterDisablesTheEntry()
    {
        var model = Parse("""
            Describe "config store" {
                It "legacy path" -Skip {
                    Add-Content -Path "/var/log/legacy.log" -Value "x"
                }
            }
            """);
        var entries = new PsTestFrameworkRegistry().ExtractEntries(model);
        var entry = Assert.Single(entries);
        Assert.False(entry.Enabled);
        Assert.EndsWith("legacy path", entry.TestId);
    }

    [Fact]
    public void PlainFunctionFileDoesNotAdapt()
    {
        var model = Parse("""
            function Load-Config {
                Get-Content -Path "/etc/app/config.json"
            }
            """);
        var registry = new PsTestFrameworkRegistry();
        Assert.Empty(registry.Detect(model));
        Assert.Empty(registry.ExtractEntries(model));
    }
}
