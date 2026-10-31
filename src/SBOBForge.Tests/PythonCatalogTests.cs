using System.Linq;
using SBOBForge.Catalog;
using SBOBForge.Core.Model;
using SBOBForge.Parsing;
using Xunit;

namespace SBOBForge.Tests;

public class PythonCatalogTests
{
    private static LoweredInvocation? Lower(
        string? receiver, string method, params CallArg[] args) =>
        PythonCatalogLowering.Lower(new InvocationDecl(receiver, method, 1, args));

    [Fact]
    public void CatalogLoadsAndHasLockEntryName()
    {
        var catalog = PythonCatalogLowering.Catalog;
        Assert.Equal("python", catalog.Id);
        Assert.Equal(1, catalog.Version);
        Assert.NotEmpty(catalog.Rules);
        Assert.NotEmpty(PythonCatalog.ComputeDefaultHash());
    }

    [Fact]
    public void OpenModeStringsMapToFlagSetsIncludingBinaryForms()
    {
        var read = Lower(null, "open", CallArg.Literal("/etc/app/config.json"));
        Assert.Equal(new[] { "O_RDONLY" }, read!.Flags);

        var append = Lower(null, "open", CallArg.Literal("/var/log/app.log"), CallArg.Literal("a"));
        Assert.Equal(new[] { "O_WRONLY", "O_CREAT", "O_APPEND" }, append!.Flags);

        // binary and text flags are ignored (not a syscall difference)
        var readBinary = Lower(null, "open", CallArg.Literal("/var/log/app.log"), CallArg.Literal("rb"));
        Assert.Equal(new[] { "O_RDONLY" }, readBinary!.Flags);

        var dirs = Lower("os", "makedirs", CallArg.Literal("/var/cache/app"));
        Assert.Equal(new[] { "O_DIRECTORY", "O_CREAT" }, dirs!.Flags);
    }

    [Fact]
    public void SubprocessFormsLowerArgvAndShell()
    {
        var argv = Lower("subprocess", "run",
            new CallArg(ArgKind.List, "git\u0001status"));
        Assert.Equal(BehaviorPrimitive.Exec, argv!.Primitive);
        Assert.Equal("git", argv.Path);
        Assert.Equal(new[] { "status" }, argv.Args);

        var shell = Lower("subprocess", "run", CallArg.Literal("git status"),
            new CallArg(ArgKind.Identifier, "True", "shell"));
        Assert.Equal("/bin/sh", shell!.Path);
        Assert.Equal(new[] { "-c", "git status" }, shell.Args);

        var system = Lower("os", "system", CallArg.Literal("ls /tmp"));
        Assert.Equal("/bin/sh", system!.Path);
        Assert.Equal(new[] { "-c", "ls /tmp" }, system.Args);
    }

    [Fact]
    public void PartiallyLiteralArgvAnchorsArgv0()
    {
        // argv0-anchoring (TDS 5.2): literal program name + dynamic later args
        // lifts instead of quarantining the whole exec.
        var anchored = Lower("subprocess", "run", new CallArg(ArgKind.PartialList, "git"));
        Assert.Equal(LiftOutcome.Event, anchored!.Outcome);
        Assert.Equal(BehaviorPrimitive.Exec, anchored.Primitive);
        Assert.Equal("git", anchored.Path);
        Assert.Empty(anchored.Args);
        Assert.False(anchored.ArgsAllLiteral); // pipeline emits [argv0, ⋯⋯]

        // fully literal lists keep exact argv (List, not PartialList)
        var literal = Lower("subprocess", "run", new CallArg(ArgKind.List, "git\u0001status"));
        Assert.True(literal!.ArgsAllLiteral);
        Assert.Equal(new[] { "status" }, literal.Args);
    }

    [Fact]
    public void NetworkRulesLowerUrlsConnectionsAndListeners()
    {
        var get = Lower("requests", "get", CallArg.Literal("https://api.example.com/status"));
        Assert.Equal("api.example.com", get!.DnsName);
        Assert.Equal(443, get.Port);

        var urlopen = Lower("urllib.request", "urlopen", CallArg.Literal("http://api.example.com/health"));
        Assert.Equal("api.example.com", urlopen!.DnsName);
        Assert.Equal(80, urlopen.Port);

        var connect = Lower("socket", "create_connection",
            new CallArg(ArgKind.List, "api.example.com\u0001443"));
        Assert.Equal("api.example.com", connect!.DnsName);
        Assert.Equal(443, connect.Port);

        var https = Lower(null, "HTTPSConnection", CallArg.Literal("api.example.com"));
        Assert.Equal(443, https!.Port); // defaultPort from the rule

        var server = Lower(null, "HTTPServer",
            new CallArg(ArgKind.List, "0.0.0.0\u00018080"), new CallArg(ArgKind.Identifier, "handler"));
        Assert.Equal(BehaviorPrimitive.Listen, server!.Primitive);
        Assert.Equal(8080, server.Port);
        Assert.Equal(NetDirection.Ingress, server.Direction);

        var bind = Lower("sock", "bind", new CallArg(ArgKind.List, "0.0.0.0\u00018080"));
        Assert.Equal(8080, bind!.Port);

        var udp = Lower(null, "UDPServer", new CallArg(ArgKind.List, "0.0.0.0\u00019090"),
            new CallArg(ArgKind.Identifier, "handler"));
        Assert.Equal("UDP", udp!.Protocol);
    }

    [Fact]
    public void RemoveIsUnrepresentableAndNonLiteralPathsQuarantine()
    {
        var remove = Lower("os", "remove", CallArg.Literal("/tmp/x"));
        Assert.Equal(LiftOutcome.Unrepresentable, remove!.Outcome);
        Assert.Equal("delete", remove.UnrepresentablePrimitive);

        var quarantined = Lower(null, "open", new CallArg(ArgKind.Interpolated, "/users/{uid}.json"));
        Assert.Equal(LiftOutcome.Quarantine, quarantined!.Outcome);
        Assert.Contains("interpolated", quarantined.Reason);

        Assert.Null(Lower("os", "listdir", CallArg.Literal("/tmp")));
    }
}
