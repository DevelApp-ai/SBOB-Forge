using System.Linq;
using SBOBForge.Catalog;
using SBOBForge.Core.Model;
using SBOBForge.Parsing;
using Xunit;

namespace SBOBForge.Tests;

public class BashCatalogTests
{
    private static LoweredInvocation? Lower(
        string? receiver, string method, params CallArg[] args) =>
        BashCatalogLowering.Lower(new InvocationDecl(receiver, method, 1, args));

    [Fact]
    public void CatalogLoadsAndHasLockEntryName()
    {
        var catalog = BashCatalogLowering.Catalog;
        Assert.Equal("bash", catalog.Id);
        Assert.Equal(1, catalog.Version);
        Assert.NotEmpty(catalog.Rules);
        Assert.Equal("bash-catalog", BashCatalog.LockEntryName);
        Assert.NotEmpty(BashCatalog.ComputeDefaultHash());
    }

    [Fact]
    public void CommandWordLiftsProgramAndExactArgv()
    {
        // `git status` → program git, literal argv → exact args (argv0 policy applies downstream)
        var git = Lower(null, "git", CallArg.Literal("status"));
        Assert.Equal(BehaviorPrimitive.Exec, git!.Primitive);
        Assert.Equal("git", git.Path);
        Assert.Equal(new[] { "status" }, git.Args);
        Assert.True(git.ArgsAllLiteral);

        // any dynamic argument argv0-anchors
        var partial = Lower(null, "git", new CallArg(ArgKind.Identifier, "$branch"));
        Assert.Equal("git", partial!.Path);
        Assert.False(partial.ArgsAllLiteral);
    }

    [Fact]
    public void CurlAndWgetLowerToEgressFromUrlLiterals()
    {
        var curl = Lower(null, "curl", CallArg.Literal("https://api.example.com/status"));
        Assert.Equal(BehaviorPrimitive.Connect, curl!.Primitive);
        Assert.Equal("api.example.com", curl.DnsName);
        Assert.Equal(443, curl.Port);

        var wget = Lower(null, "wget", CallArg.Literal("http://insecure.internal/health"));
        Assert.Equal(80, wget!.Port);

        // any dynamic argument quarantines instead of best-effort URL extraction
        var dynamic = Lower(null, "curl", new CallArg(ArgKind.Identifier, "$url"));
        Assert.Equal(LiftOutcome.Quarantine, dynamic!.Outcome);

        // literal args but no absolute http(s) URL quarantines
        var noUrl = Lower(null, "curl", CallArg.Literal("-s"), CallArg.Literal("file:///etc/x"));
        Assert.Equal(LiftOutcome.Quarantine, noUrl!.Outcome);
    }

    [Fact]
    public void NcAndSshLowerToEgressHostPort()
    {
        var nc = Lower(null, "nc", CallArg.Literal("-z"), CallArg.Literal("db.internal"), CallArg.Literal("5432"));
        Assert.Equal(BehaviorPrimitive.Connect, nc!.Primitive);
        Assert.Equal("db.internal", nc.DnsName);
        Assert.Equal(5432, nc.Port);

        // ssh host → default port 22 (TDS 7.2)
        var ssh = Lower(null, "ssh", CallArg.Literal("deploy.internal"), CallArg.Literal("systemctl status app"));
        Assert.Equal(BehaviorPrimitive.Connect, ssh!.Primitive);
        Assert.Equal("deploy.internal", ssh.DnsName);
        Assert.Equal(22, ssh.Port);
    }

    [Fact]
    public void DeletesAndRedirections()
    {
        var rm = Lower(null, "rm", CallArg.Literal("%PackageRoot%/cache/last.json"));
        Assert.Equal(LiftOutcome.Unrepresentable, rm!.Outcome);
        Assert.Equal("delete", rm.UnrepresentablePrimitive);

        var rmdir = Lower(null, "rmdir", CallArg.Literal("/var/empty"));
        Assert.Equal("delete", rmdir!.UnrepresentablePrimitive);

        var truncate = Lower(null, ">", CallArg.Literal("%PackageRoot%/cache/last.json"));
        Assert.Equal(BehaviorPrimitive.Open, truncate!.Primitive);
        Assert.Equal(new[] { "O_WRONLY", "O_CREAT", "O_TRUNC" }, truncate.Flags);

        var append = Lower(null, ">>", CallArg.Literal("/var/log/bash-app.log"));
        Assert.Equal(new[] { "O_WRONLY", "O_CREAT", "O_APPEND" }, append!.Flags);
    }
}
