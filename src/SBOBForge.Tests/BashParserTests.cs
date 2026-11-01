using System.Linq;
using SBOBForge.Core;
using SBOBForge.Parsing;
using Xunit;

namespace SBOBForge.Tests;

public class BashParserTests
{
    private static CodeModel Parse(string source) =>
        new BashSourceParser().Parse("sut/bash/status.sh", source);

    [Fact]
    public void FunctionBlocksBothSyntaxesBecomeMethods()
    {
        var model = Parse("""
            run_git_status() {
                git status
            }

            function fetch_status {
                curl "https://api.example.com/status"
            }
            """);
        var type = Assert.Single(model.Types);
        Assert.Equal(2, type.Methods.Count);

        var git = Assert.Single(type.Methods, m => m.Name == "run_git_status");
        var status = Assert.Single(git.Invocations, i => i.Method == "git");
        Assert.Equal("status", status.Args[0].Value);
        Assert.Equal(ArgKind.Literal, status.Args[0].Kind);

        var fetch = Assert.Single(type.Methods, m => m.Name == "fetch_status");
        var curl = Assert.Single(fetch.Invocations, i => i.Method == "curl");
        Assert.Equal("https://api.example.com/status", curl.Args[0].Value);
    }

    [Fact]
    public void VariablesCommandSubstitutionAndRedirections()
    {
        var model = Parse("""
            save_cache() {
                echo $Payload > "%PackageRoot%/cache/last.json"
            }

            append_dynamic_log() {
                echo "entry" >> "/var/log/$Name.log"
            }

            eval_output() {
                OUT=$(git status)
                echo "${OUT}"
            }
            """);
        var type = Assert.Single(model.Types);

        // `$Payload` in an unquoted echo is an identifier arg; the target is a literal path
        var save = Assert.Single(type.Methods, m => m.Name == "save_cache");
        var echo = Assert.Single(save.Invocations, i => i.Method == "echo");
        Assert.Equal(ArgKind.Identifier, echo.Args[0].Kind);
        Assert.Equal("$Payload", echo.Args[0].Value);
        var truncate = Assert.Single(save.Invocations, i => i.Method == ">");
        Assert.Equal("%PackageRoot%/cache/last.json", truncate.Args[0].Value);

        // `$Name` inside the double-quoted target makes the open path interpolated
        var dynamic = Assert.Single(type.Methods, m => m.Name == "append_dynamic_log");
        var append = Assert.Single(dynamic.Invocations, i => i.Method == ">>");
        Assert.Equal(ArgKind.Interpolated, append.Args[0].Kind);

        // $(…) command substitution is one opaque expression token: "OUT=" assignment
        // word skipped, the substitution is not scanned as a nested command
        var eval = Assert.Single(type.Methods, m => m.Name == "eval_output");
        var outEcho = Assert.Single(eval.Invocations);
        Assert.Equal("echo", outEcho.Method);
        Assert.Equal(ArgKind.Interpolated, outEcho.Args[0].Kind);
    }

    [Fact]
    public void BatsBlocksProduceEntryAttributes()
    {
        var model = Parse("""
            @test "runs git status" {
                run_git_status
            }

            @test "legacy path" {
                skip
                rm "/tmp/legacy-cache"
            }
            """);
        var type = Assert.Single(model.Types);
        Assert.Equal(2, type.Methods.Count);

        var runs = Assert.Single(type.Methods, m => m.Name.StartsWith("runs git status"));
        var attr = Assert.Single(runs.Attributes);
        Assert.Equal("sh.entry", attr.Name);
        Assert.Equal("runs git status", attr.Args.Single(a => a.IsNamed("title")).Value);
        Assert.Equal("run", attr.Args.Single(a => a.IsNamed("state")).Value);

        var legacy = Assert.Single(type.Methods, m => m.Name.StartsWith("legacy path"));
        Assert.Equal("skip",
            Assert.Single(legacy.Attributes).Args.Single(a => a.IsNamed("state")).Value);
    }

    [Fact]
    public void UnbalancedBraceIsAHardError()
    {
        Assert.Throws<ParseHardError>(() => Parse("run_git_status() {\n    git status\n"));
    }
}
