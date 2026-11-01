using System.Linq;
using SBOBForge.Core;
using SBOBForge.Parsing;
using Xunit;

namespace SBOBForge.Tests;

public class PowerShellParserTests
{
    private static CodeModel Parse(string source) =>
        new PowerShellSourceParser().Parse("sut/ps/config-store.ps1", source);

    [Fact]
    public void FunctionBlocksBecomeMethods()
    {
        var model = Parse("""
            function Load-Config {
                $raw = Get-Content -Path "/etc/app/config.json" -Raw
                return ($raw | ConvertFrom-Json)
            }

            function Save-Cache {
                param($Payload)
                Set-Content -Path "%PackageRoot%/cache/last.json" -Value $Payload
            }
            """);
        var type = Assert.Single(model.Types);
        Assert.Equal(2, type.Methods.Count);

        var load = Assert.Single(type.Methods, m => m.Name == "Load-Config");
        // Get-Content after `=` is in command position; -Path is a named literal arg
        var read = Assert.Single(load.Invocations, i => i.Method == "Get-Content");
        Assert.Equal("/etc/app/config.json", read.Args[0].Value);
        Assert.True(read.Args[0].IsNamed("Path"));

        // command word after a closed param(...) block still lands in command position
        var save = Assert.Single(type.Methods, m => m.Name == "Save-Cache");
        var write = Assert.Single(save.Invocations, i => i.Method == "Set-Content");
        Assert.Equal("%PackageRoot%/cache/last.json", write.Args[0].Value);
    }

    [Fact]
    public void PesterBlocksProduceEntryAttributes()
    {
        var model = Parse("""
            Describe "config store" {
                Context "file system" {
                    It "loads config" {
                        Load-Config
                    }

                    It "legacy path" -Skip {
                        Add-Content -Path "/var/log/legacy.log" -Value "x"
                    }
                }
            }
            """);
        var type = Assert.Single(model.Types);
        Assert.Equal(2, type.Methods.Count);

        var loads = Assert.Single(type.Methods, m => m.Name.Contains("loads config"));
        var attr = Assert.Single(loads.Attributes);
        Assert.Equal("ps.entry", attr.Name);
        Assert.Equal("loads config", attr.Args.Single(a => a.IsNamed("title")).Value);
        Assert.Equal("config store > file system > loads config",
            attr.Args.Single(a => a.IsNamed("full")).Value);
        Assert.Equal("run", attr.Args.Single(a => a.IsNamed("state")).Value);

        var legacy = Assert.Single(type.Methods, m => m.Name.Contains("legacy path"));
        Assert.Equal("skip",
            Assert.Single(legacy.Attributes).Args.Single(a => a.IsNamed("state")).Value);
    }

    [Fact]
    public void CallOperatorNewObjectAndRedirections()
    {
        var model = Parse("""
            function Invoke-GitStatus {
                & "git" "status"
            }

            function Start-GitStatus {
                Start-Process -FilePath "git" -ArgumentList "status" -NoNewWindow
            }

            function Append-Log {
                Write-Output "entry" >> "/var/log/ps-app.log"
            }
            """);
        var type = Assert.Single(model.Types);

        var git = Assert.Single(type.Methods, m => m.Name == "Invoke-GitStatus");
        var call = Assert.Single(git.Invocations, i => i.Method == "&");
        Assert.Equal("git", call.Args[0].Value);
        Assert.Equal("status", call.Args[1].Value);

        var start = Assert.Single(type.Methods, m => m.Name == "Start-GitStatus");
        var proc = Assert.Single(start.Invocations, i => i.Method == "Start-Process");
        Assert.Equal("git", proc.Args.Single(a => a.IsNamed("FilePath")).Value);
        // -NoNewWindow is a switch parameter → named boolean true
        Assert.Equal("true", proc.Args.Single(a => a.IsNamed("NoNewWindow")).Value);

        var log = Assert.Single(type.Methods, m => m.Name == "Append-Log");
        var redirect = Assert.Single(log.Invocations, i => i.Method == ">>");
        Assert.Equal("/var/log/ps-app.log", redirect.Args[0].Value);
    }

    [Fact]
    public void InterpolatedStringsAndVariables()
    {
        var model = Parse("""
            function Add-DynamicLog {
                param($Name)
                Add-Content -Path "/var/log/ps-$Name.log" -Value "entry"
            }
            """);
        var type = Assert.Single(model.Types);
        var log = Assert.Single(type.Methods, m => m.Name == "Add-DynamicLog");
        var add = Assert.Single(log.Invocations, i => i.Method == "Add-Content");
        // $Name inside the double-quoted string marks it interpolated
        Assert.Equal(ArgKind.Interpolated, add.Args[0].Kind);

        // param(…) blocks are tolerated as a command-shaped expression, not a modeled parameter list
        var save = Assert.Single(log.Invocations, i => i.Method == "param");
        Assert.Equal(ArgKind.Expression, Assert.Single(save.Args).Kind);
    }

    [Fact]
    public void UnbalancedBraceIsAHardError()
    {
        Assert.Throws<ParseHardError>(() => Parse("function A {\n    Get-Content x\n"));
    }
}
