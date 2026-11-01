using System.Linq;
using SBOBForge.Catalog;
using SBOBForge.Core.Model;
using SBOBForge.Parsing;
using Xunit;

namespace SBOBForge.Tests;

public class CCatalogTests
{
    private static LoweredInvocation? Lower(
        string? receiver, string method, params CallArg[] args) =>
        CCatalogLowering.Lower(new InvocationDecl(receiver, method, 1, args));

    [Fact]
    public void CatalogLoadsAndHasLockEntryName()
    {
        var catalog = CCatalogLowering.Catalog;
        Assert.Equal("c", catalog.Id);
        Assert.Equal(1, catalog.Version);
        Assert.NotEmpty(catalog.Rules);
        Assert.Equal("c-catalog", CCatalog.LockEntryName);
        Assert.NotEmpty(CCatalog.ComputeDefaultHash());
    }

    [Fact]
    public void FopenModeStringsMapToSpecFlags()
    {
        var read = Lower(null, "fopen", CallArg.Literal("/etc/app/config.json"), CallArg.Literal("r"));
        Assert.Equal(new[] { "O_RDONLY" }, read!.Flags);

        var append = Lower(null, "fopen", CallArg.Literal("/var/log/c-app.log"), CallArg.Literal("a"));
        Assert.Equal(new[] { "O_WRONLY", "O_CREAT", "O_APPEND" }, append!.Flags);
    }

    [Fact]
    public void OpenFlagMasksLiftAndUnknownFlagsQuarantine()
    {
        var masked = Lower(null, "open", CallArg.Literal("/var/log/c-app.log"),
            new CallArg(ArgKind.Identifier, "O_WRONLY|O_CREAT|O_APPEND"));
        Assert.Equal(LiftOutcome.Event, masked!.Outcome);
        Assert.Equal(new[] { "O_WRONLY", "O_CREAT", "O_APPEND" }, masked.Flags);

        var unknown = Lower(null, "open", CallArg.Literal("/tmp/x"),
            new CallArg(ArgKind.Identifier, "O_WRONLY|O_WEIRD"));
        Assert.Equal(LiftOutcome.Quarantine, unknown!.Outcome);
        Assert.Contains("O_WEIRD", unknown.Reason);

        var dynamic = Lower(null, "open", new CallArg(ArgKind.Expression, ""),
            new CallArg(ArgKind.Identifier, "O_WRONLY"));
        Assert.Equal(LiftOutcome.Quarantine, dynamic!.Outcome);
    }

    [Fact]
    public void ExecFamilyLiftsLiteralArgvAndAnchorsPartialArgv0()
    {
        // execvp(path, {"git", "status"}) → exact argv
        var full = Lower(null, "execvp", CallArg.Literal("/usr/bin/git"),
            new CallArg(ArgKind.List, "/usr/bin/git\u0001status"));
        Assert.Equal(LiftOutcome.Event, full!.Outcome);
        Assert.Equal("/usr/bin/git", full.Path);
        Assert.Equal(new[] { "status" }, full.Args);
        Assert.True(full.ArgsAllLiteral);

        // execvp(path, {"git", dynamic}) → argv0-anchored wildcard args (TDS 5.2)
        var partial = Lower(null, "execvp", CallArg.Literal("/usr/bin/git"),
            new CallArg(ArgKind.PartialList, "/usr/bin/git"));
        Assert.Equal(LiftOutcome.Event, partial!.Outcome);
        Assert.Equal("/usr/bin/git", partial.Path);
        Assert.False(partial.ArgsAllLiteral);

        // execvp(dynamic_path, dynamic_argv) → neither statically resolvable
        var dyn = Lower(null, "execvp", new CallArg(ArgKind.Identifier, "argv0"),
            new CallArg(ArgKind.Identifier, "argv"));
        Assert.Equal(LiftOutcome.Quarantine, dyn!.Outcome);

        // execvp("/usr/bin/git", argv) → program knowable, args wildcard
        var program = Lower(null, "execv", CallArg.Literal("/usr/bin/git"),
            new CallArg(ArgKind.Identifier, "argv"));
        Assert.Equal(LiftOutcome.Event, program!.Outcome);
        Assert.Equal("/usr/bin/git", program.Path);
        Assert.False(program.ArgsAllLiteral);
    }

    [Fact]
    public void SystemAndPopenAreShellMediated()
    {
        var sh = Lower(null, "system", CallArg.Literal("git status"));
        Assert.Equal(BehaviorPrimitive.Exec, sh!.Primitive);
        Assert.Equal("/bin/sh", sh.Path);
        Assert.Equal(new[] { "-c", "git status" }, sh.Args);
    }

    [Fact]
    public void GetAddrInfoResolvesHostAndNumericService()
    {
        var resolved = Lower(null, "getaddrinfo",
            CallArg.Literal("api.example.com"), CallArg.Literal("443"));
        Assert.Equal(BehaviorPrimitive.Connect, resolved!.Primitive);
        Assert.Equal("api.example.com", resolved.DnsName);
        Assert.Equal(443, resolved.Port);

        // NULL service: host resolvable, port unknown
        var nullService = Lower(null, "getaddrinfo",
            CallArg.Literal("internal.local"), new CallArg(ArgKind.Identifier, "NULL"));
        Assert.Equal(LiftOutcome.Event, nullService!.Outcome);
        Assert.Null(nullService.Port);

        // service names are not resolved in v1
        var named = Lower(null, "getaddrinfo",
            CallArg.Literal("internal.local"), CallArg.Literal("https"));
        Assert.Equal(LiftOutcome.Quarantine, named!.Outcome);
    }

    [Fact]
    public void ConnectUsesHoistedHostPortNamedArgs()
    {
        var hoisted = Lower(null, "connect",
            new CallArg(ArgKind.Identifier, "3"),
            new CallArg(ArgKind.Expression, ""),
            new CallArg(ArgKind.Literal, "10.0.0.5", "host"),
            new CallArg(ArgKind.Literal, "443", "port"));
        Assert.Equal(BehaviorPrimitive.Connect, hoisted!.Primitive);
        Assert.Equal("10.0.0.5", hoisted.DnsName);
        Assert.Equal(443, hoisted.Port);

        // no hoisted literals → the socket address is not statically resolvable
        var bare = Lower(null, "connect", new CallArg(ArgKind.Identifier, "3"));
        Assert.Equal(LiftOutcome.Quarantine, bare!.Outcome);
    }

    [Fact]
    public void ListenRequiresHoistedPort()
    {
        var hoisted = Lower(null, "listen",
            new CallArg(ArgKind.Identifier, "4"),
            new CallArg(ArgKind.Literal, "8080", "port"));
        Assert.Equal(BehaviorPrimitive.Listen, hoisted!.Primitive);
        Assert.Equal(8080, hoisted.Port);

        var bare = Lower(null, "listen", new CallArg(ArgKind.Identifier, "4"));
        Assert.Equal(LiftOutcome.Quarantine, bare!.Outcome);
    }

    [Fact]
    public void DeletePrimitivesAreUnrepresentable()
    {
        var unlink = Lower(null, "unlink", CallArg.Literal("/var/cache/c-app/last.json"));
        Assert.Equal(LiftOutcome.Unrepresentable, unlink!.Outcome);
        Assert.Equal("delete", unlink.UnrepresentablePrimitive);
    }
}

public class CppCatalogTests
{
    private static LoweredInvocation? Lower(
        string? receiver, string method, params CallArg[] args) =>
        CppCatalogLowering.Lower(new InvocationDecl(receiver, method, 1, args));

    [Fact]
    public void CatalogLoadsAndHasLockEntryName()
    {
        var catalog = CppCatalogLowering.Catalog;
        Assert.Equal("cpp", catalog.Id);
        Assert.Equal(1, catalog.Version);
        Assert.NotEmpty(catalog.Rules);
        Assert.Equal("cpp-catalog", CppCatalog.LockEntryName);
        Assert.NotEmpty(CppCatalog.ComputeDefaultHash());
    }

    [Fact]
    public void StdSystemIsShellMediated()
    {
        var sh = Lower("std", "system", CallArg.Literal("git status"));
        Assert.Equal(BehaviorPrimitive.Exec, sh!.Primitive);
        Assert.Equal("/bin/sh", sh.Path);
        Assert.Equal(new[] { "-c", "git status" }, sh.Args);
    }

    [Fact]
    public void IfstreamConstructionAndOpenAreReadOnly()
    {
        var ctor = Lower("std", "ifstream", CallArg.Literal("/etc/app/config.json"));
        Assert.Equal(new[] { "O_RDONLY" }, ctor!.Flags);

        // tracked member call: receiver is the canonical variable type
        var open = Lower("std::ifstream", "open", CallArg.Literal("/etc/app/config.json"));
        Assert.Equal(new[] { "O_RDONLY" }, open!.Flags);
    }

    [Fact]
    public void OfstreamMasksMapToTruncOrAppend()
    {
        var plain = Lower("std", "ofstream", CallArg.Literal("/var/log/cpp-app.log"));
        Assert.Equal(new[] { "O_WRONLY", "O_CREAT", "O_TRUNC" }, plain!.Flags);

        var append = Lower("std", "ofstream", CallArg.Literal("/var/log/cpp-app.log"),
            new CallArg(ArgKind.Identifier, "std::ios::app"));
        Assert.Equal(new[] { "O_WRONLY", "O_CREAT", "O_APPEND" }, append!.Flags);

        var binaryAppend = Lower("std", "ofstream", CallArg.Literal("/var/log/cpp-app.log"),
            new CallArg(ArgKind.Identifier, "std::ios::out|std::ios::binary|std::ios::app"));
        Assert.Equal(new[] { "O_WRONLY", "O_CREAT", "O_APPEND" }, binaryAppend!.Flags);

        var unresolvable = Lower("std", "ofstream", CallArg.Literal("/tmp/x"),
            new CallArg(ArgKind.Identifier, "std::ios::ate"));
        Assert.Equal(LiftOutcome.Quarantine, unresolvable!.Outcome);
    }

    [Fact]
    public void FstreamMasksMapInOutOfTruncApp()
    {
        var rw = Lower("std", "fstream", CallArg.Literal("/var/lib/cpp-app/db.dat"),
            new CallArg(ArgKind.Identifier, "std::ios::in|std::ios::out"));
        Assert.Equal(new[] { "O_RDWR" }, rw!.Flags);

        var trunc = Lower("std", "fstream", CallArg.Literal("/var/lib/cpp-app/db.dat"),
            new CallArg(ArgKind.Identifier, "std::ios::in|std::ios::out|std::ios::trunc|std::ios::binary"));
        Assert.Equal(new[] { "O_RDWR", "O_TRUNC" }, trunc!.Flags);

        var neither = Lower("std", "fstream", CallArg.Literal("/tmp/x"),
            new CallArg(ArgKind.Identifier, "std::ios::binary"));
        Assert.Equal(LiftOutcome.Quarantine, neither!.Outcome);
    }

    [Fact]
    public void FilesystemRulesMapCreateAndDeletes()
    {
        var mkdir = Lower("std::filesystem", "create_directory", CallArg.Literal("/var/lib/cpp-app"));
        Assert.Equal(BehaviorPrimitive.Open, mkdir!.Primitive);
        Assert.Equal(new[] { "O_DIRECTORY", "O_CREAT" }, mkdir.Flags);

        var rm = Lower("std::filesystem", "remove_all", CallArg.Literal("/var/cache/cpp-app/stale"));
        Assert.Equal(LiftOutcome.Unrepresentable, rm!.Outcome);
        Assert.Equal("delete", rm.UnrepresentablePrimitive);
    }

    [Fact]
    public void AsioResolverResolveIsEgressHostPort()
    {
        var resolved = Lower("boost::asio::ip::tcp::resolver", "resolve",
            CallArg.Literal("api.example.com"), CallArg.Literal("443"));
        Assert.Equal(BehaviorPrimitive.Connect, resolved!.Primitive);
        Assert.Equal("api.example.com", resolved.DnsName);
        Assert.Equal(443, resolved.Port);
    }

    [Fact]
    public void ConnectAndListenUseHoistedNamedArgs()
    {
        var connect = Lower(null, "connect",
            new CallArg(ArgKind.Identifier, "3"),
            new CallArg(ArgKind.Literal, "192.168.1.10", "host"),
            new CallArg(ArgKind.Literal, "9443", "port"));
        Assert.Equal(BehaviorPrimitive.Connect, connect!.Primitive);
        Assert.Equal("192.168.1.10", connect.DnsName);
        Assert.Equal(9443, connect.Port);

        var listen = Lower(null, "listen",
            new CallArg(ArgKind.Identifier, "4"),
            new CallArg(ArgKind.Literal, "8080", "port"));
        Assert.Equal(BehaviorPrimitive.Listen, listen!.Primitive);
        Assert.Equal(8080, listen.Port);
    }

    [Fact]
    public void ReceiverScopingKeepsUnrelatedCallsUnmatched()
    {
        // remove on a non-filesystem receiver is not a delete primitive
        Assert.Null(Lower("app::Store", "remove", CallArg.Literal("/tmp/x")));
        // ifstream with a wrong receiver does not match the std rule
        Assert.Null(Lower("my", "ifstream", CallArg.Literal("/tmp/x")));
    }
}
