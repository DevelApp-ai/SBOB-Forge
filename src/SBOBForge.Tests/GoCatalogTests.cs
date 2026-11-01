using System.Linq;
using SBOBForge.Catalog;
using SBOBForge.Core.Model;
using SBOBForge.Parsing;
using Xunit;

namespace SBOBForge.Tests;

public class GoCatalogTests
{
    private static LoweredInvocation? Lower(
        string? receiver, string method, params CallArg[] args) =>
        GoCatalogLowering.Lower(new InvocationDecl(receiver, method, 1, args));

    [Fact]
    public void CatalogLoadsAndHasLockEntryName()
    {
        var catalog = GoCatalogLowering.Catalog;
        Assert.Equal("go", catalog.Id);
        Assert.Equal(1, catalog.Version);
        Assert.NotEmpty(catalog.Rules);
        Assert.Equal("go-catalog", GoCatalog.LockEntryName);
        Assert.NotEmpty(GoCatalog.ComputeDefaultHash());
    }

    [Fact]
    public void OsFileOpsLowerToSpecFlagSets()
    {
        var read = Lower("os", "ReadFile", CallArg.Literal("/etc/app/config.json"));
        Assert.Equal(new[] { "O_RDONLY" }, read!.Flags);

        var write = Lower("os", "WriteFile", CallArg.Literal("/var/log/app.log"));
        Assert.Equal(new[] { "O_WRONLY", "O_CREAT", "O_TRUNC" }, write!.Flags);

        var dirs = Lower("os", "MkdirAll", CallArg.Literal("/var/cache/app"));
        Assert.Equal(new[] { "O_DIRECTORY", "O_CREAT" }, dirs!.Flags);
    }

    [Fact]
    public void OpenFileFlagMaskResolvesToCanonicalOrder()
    {
        // os.O_APPEND|os.O_WRONLY|os.O_CREATE (any source order) → canonical spec order
        var append = Lower("os", "OpenFile",
            CallArg.Literal("/var/log/go-app.log"),
            new CallArg(ArgKind.Identifier, "O_APPEND|O_WRONLY|O_CREATE"));
        Assert.Equal(BehaviorPrimitive.Open, append!.Primitive);
        Assert.Equal(new[] { "O_WRONLY", "O_CREAT", "O_APPEND" }, append.Flags);

        // unknown flag constants quarantine instead of guessing
        var weird = Lower("os", "OpenFile",
            CallArg.Literal("/var/log/go-app.log"),
            new CallArg(ArgKind.Identifier, "O_LARGEFILE|O_WRONLY"));
        Assert.Equal(LiftOutcome.Quarantine, weird!.Outcome);

        // non-constant path quarantines
        var dynamic = Lower("os", "OpenFile",
            new CallArg(ArgKind.Identifier, "path"),
            new CallArg(ArgKind.Identifier, "O_WRONLY"));
        Assert.Equal(LiftOutcome.Quarantine, dynamic!.Outcome);
    }

    [Fact]
    public void ExecCommandLowersArgv()
    {
        var git = Lower("exec", "Command", CallArg.Literal("git"), CallArg.Literal("status"));
        Assert.Equal(BehaviorPrimitive.Exec, git!.Primitive);
        Assert.Equal("git", git.Path);
        Assert.Equal(new[] { "status" }, git.Args);
        Assert.True(git.ArgsAllLiteral);

        // dynamic argv entries keep the literal argv0 but mark args non-literal
        var partial = Lower("exec", "Command",
            CallArg.Literal("git"), new CallArg(ArgKind.Identifier, "branch"));
        Assert.Equal("git", partial!.Path);
        Assert.False(partial.ArgsAllLiteral);
    }

    [Fact]
    public void NetAndHttpLowerToEgressIngressEvents()
    {
        var egress = Lower("http", "Get", CallArg.Literal("https://api.example.com/status"));
        Assert.Equal(BehaviorPrimitive.Connect, egress!.Primitive);
        Assert.Equal("api.example.com", egress.DnsName);
        Assert.Equal(443, egress.Port);
        Assert.Equal("TCP", egress.Protocol);

        var listen = Lower("net", "Listen",
            CallArg.Literal("tcp"), CallArg.Literal(":8080"));
        Assert.Equal(BehaviorPrimitive.Listen, listen!.Primitive);
        Assert.Equal(NetDirection.Ingress, listen.Direction);
        Assert.Equal(8080, listen.Port);
        Assert.Equal("TCP", listen.Protocol);

        var dial = Lower("net", "Dial",
            CallArg.Literal("udp"), CallArg.Literal("syslog.internal:514"));
        Assert.Equal(NetDirection.Egress, dial!.Direction);
        Assert.Equal("syslog.internal", dial.DnsName);
        Assert.Equal(514, dial.Port);
        Assert.Equal("UDP", dial.Protocol);

        // non host:port listen addresses quarantine (no guessing)
        var bad = Lower("net", "Listen", CallArg.Literal("tcp"), CallArg.Literal("localhost"));
        Assert.Equal(LiftOutcome.Quarantine, bad!.Outcome);
    }

    [Fact]
    public void DeletesAreUnrepresentableNotDropped()
    {
        var remove = Lower("os", "Remove", CallArg.Literal("/tmp/cache"));
        Assert.Equal(LiftOutcome.Unrepresentable, remove!.Outcome);
        Assert.Equal("delete", remove.UnrepresentablePrimitive);

        var removeAll = Lower("os", "RemoveAll", CallArg.Literal("/tmp"));
        Assert.Equal("delete", removeAll!.UnrepresentablePrimitive);
    }
}
