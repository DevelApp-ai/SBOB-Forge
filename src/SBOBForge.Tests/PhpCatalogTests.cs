using System.Linq;
using SBOBForge.Catalog;
using SBOBForge.Core.Model;
using SBOBForge.Parsing;
using Xunit;

namespace SBOBForge.Tests;

public class PhpCatalogTests
{
    private static LoweredInvocation? Lower(
        string? receiver, string method, params CallArg[] args) =>
        PhpCatalogLowering.Lower(new InvocationDecl(receiver, method, 1, args));

    [Fact]
    public void CatalogLoadsAndHasLockEntryName()
    {
        var catalog = PhpCatalogLowering.Catalog;
        Assert.Equal("php", catalog.Id);
        Assert.Equal(1, catalog.Version);
        Assert.NotEmpty(catalog.Rules);
        Assert.Equal("php-catalog", PhpCatalog.LockEntryName);
        Assert.NotEmpty(PhpCatalog.ComputeDefaultHash());
    }

    [Fact]
    public void FopenModeTableMapsToSpecFlags()
    {
        var read = Lower("this", "fopen", CallArg.Literal("/etc/app/config.json"), CallArg.Literal("r"));
        Assert.Equal(new[] { "O_RDONLY" }, read!.Flags);

        var append = Lower(null, "fopen", CallArg.Literal("/var/log/php-app.log"), CallArg.Literal("a"));
        Assert.Equal(new[] { "O_WRONLY", "O_CREAT", "O_APPEND" }, append!.Flags);

        // unknown mode string quarantines instead of guessing
        var weird = Lower(null, "fopen", CallArg.Literal("/tmp/x"), CallArg.Literal("z"));
        Assert.Equal(LiftOutcome.Quarantine, weird!.Outcome);
    }

    [Fact]
    public void FileGetContentsIsDualPurpose()
    {
        // absolute http(s) URL literal → egress Connect
        var url = Lower(null, "file_get_contents", CallArg.Literal("https://api.example.com/status"));
        Assert.Equal(BehaviorPrimitive.Connect, url!.Primitive);
        Assert.Equal("api.example.com", url.DnsName);
        Assert.Equal(443, url.Port);

        // filesystem path → O_RDONLY open
        var file = Lower(null, "file_get_contents", CallArg.Literal("/etc/app/config.json"));
        Assert.Equal(BehaviorPrimitive.Open, file!.Primitive);
        Assert.Equal(new[] { "O_RDONLY" }, file.Flags);

        // a non-literal path/URL quarantines with the dual-kind reason
        var dynamic = Lower(null, "file_get_contents", new CallArg(ArgKind.Expression, ""));
        Assert.Equal(LiftOutcome.Quarantine, dynamic!.Outcome);
        Assert.Contains("path/URL", dynamic.Reason);
    }

    [Fact]
    public void ExecFamilyIsShellMediatedAndLiteralFirstTokenLifts()
    {
        // exec("git status", $output) → literal first token → execPathPolicy applies downstream
        var git = Lower(null, "exec", CallArg.Literal("git status"), new CallArg(ArgKind.Identifier, "$output"));
        Assert.Equal(BehaviorPrimitive.Exec, git!.Primitive);
        Assert.Equal("git", git.Path);
        Assert.Equal(new[] { "status" }, git.Args);
        Assert.True(git.ArgsAllLiteral);

        // shell-mediated builtins have no argv in the spec → /bin/sh -c lowering
        var shell = Lower(null, "shell_exec", CallArg.Literal("git status"));
        Assert.Equal(BehaviorPrimitive.Exec, shell!.Primitive);
        Assert.Equal("git", shell.Path);

        // dynamic command quarantines
        var dynamic = Lower(null, "system", new CallArg(ArgKind.Identifier, "$cmd"));
        Assert.Equal(LiftOutcome.Quarantine, dynamic!.Outcome);
    }

    [Fact]
    public void CurlInitLowersToEgress()
    {
        var curl = Lower(null, "curl_init", CallArg.Literal("https://api.example.com/status"));
        Assert.Equal(BehaviorPrimitive.Connect, curl!.Primitive);
        Assert.Equal("api.example.com", curl.DnsName);
        Assert.Equal(443, curl.Port);

        // non-URL argument quarantines
        var bad = Lower(null, "curl_init", CallArg.Literal("/etc/app/config.json"));
        Assert.Equal(LiftOutcome.Quarantine, bad!.Outcome);
    }

    [Fact]
    public void DeletesAreUnrepresentableAndWritesTruncate()
    {
        var unlink = Lower(null, "unlink", CallArg.Literal("/var/cache/last.json"));
        Assert.Equal(LiftOutcome.Unrepresentable, unlink!.Outcome);
        Assert.Equal("delete", unlink.UnrepresentablePrimitive);

        var rmdir = Lower(null, "rmdir", CallArg.Literal("/var/cache"));
        Assert.Equal("delete", rmdir!.UnrepresentablePrimitive);

        var write = Lower(null, "file_put_contents", CallArg.Literal("/var/cache/last.json"), CallArg.Literal("{}"));
        Assert.Equal(BehaviorPrimitive.Open, write!.Primitive);
        Assert.Equal(new[] { "O_WRONLY", "O_CREAT", "O_TRUNC" }, write.Flags);
    }
}
