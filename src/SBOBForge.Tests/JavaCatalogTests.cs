using System.Linq;
using SBOBForge.Catalog;
using SBOBForge.Core.Model;
using SBOBForge.Parsing;
using Xunit;

namespace SBOBForge.Tests;

public class JavaCatalogTests
{
    private static LoweredInvocation? Lower(
        string? receiver, string method, params CallArg[] args) =>
        JavaCatalogLowering.Lower(new InvocationDecl(receiver, method, 1, args));

    [Fact]
    public void CatalogLoadsAndHasLockEntryName()
    {
        var catalog = JavaCatalogLowering.Catalog;
        Assert.Equal("java", catalog.Id);
        Assert.Equal(1, catalog.Version);
        Assert.NotEmpty(catalog.Rules);
        Assert.NotEmpty(JavaCatalog.ComputeDefaultHash());
    }

    [Fact]
    public void FilesOpenRulesMapToFlagSets()
    {
        var read = Lower("Files", "readString", CallArg.Literal("/etc/app/config.json"));
        Assert.NotNull(read);
        Assert.Equal(LiftOutcome.Event, read!.Outcome);
        Assert.Equal(BehaviorPrimitive.Open, read.Primitive);
        Assert.Equal("/etc/app/config.json", read.Path);
        Assert.Equal(new[] { "O_RDONLY" }, read.Flags);

        var write = Lower("Files", "write", CallArg.Literal("/var/log/app.log"),
            new CallArg(ArgKind.Expression, "bytes"));
        Assert.Equal(new[] { "O_WRONLY", "O_CREAT", "O_TRUNC" }, write!.Flags);
    }

    [Fact]
    public void FileOutputStreamAppendFlagSelectsFlagSet()
    {
        var truncate = Lower(null, "FileOutputStream", CallArg.Literal("/var/log/app.log"));
        Assert.Equal(new[] { "O_WRONLY", "O_CREAT", "O_TRUNC" }, truncate!.Flags);

        var append = Lower(null, "FileOutputStream", CallArg.Literal("/var/log/app.log"),
            new CallArg(ArgKind.Identifier, "true"));
        Assert.Equal(new[] { "O_WRONLY", "O_CREAT", "O_APPEND" }, append!.Flags);
    }

    [Fact]
    public void ProcessBuilderLowersArgvAndRuntimeExecSplitsTokens()
    {
        var exec = Lower(null, "ProcessBuilder", CallArg.Literal("git"), CallArg.Literal("status"));
        Assert.Equal(BehaviorPrimitive.Exec, exec!.Primitive);
        Assert.Equal("git", exec.Path);
        Assert.Equal(new[] { "status" }, exec.Args);

        var runtime = Lower("getRuntime", "exec", CallArg.Literal("git status"));
        Assert.Equal("git", runtime!.Path);
        Assert.Equal(new[] { "status" }, runtime.Args);
    }

    [Fact]
    public void NetworkRulesLowerHostPortAndListener()
    {
        var url = Lower(null, "URL", CallArg.Literal("https://api.example.com/status"));
        Assert.Equal(BehaviorPrimitive.Connect, url!.Primitive);
        Assert.Equal("api.example.com", url.DnsName);
        Assert.Equal(443, url.Port);
        Assert.Equal(NetDirection.Egress, url.Direction);

        var socket = Lower(null, "Socket", CallArg.Literal("api.example.com"), CallArg.Literal("443"));
        Assert.Equal("api.example.com", socket!.DnsName);
        Assert.Equal(443, socket.Port);

        var server = Lower(null, "ServerSocket", CallArg.Literal("8080"));
        Assert.Equal(BehaviorPrimitive.Listen, server!.Primitive);
        Assert.Equal(8080, server.Port);
        Assert.Equal(NetDirection.Ingress, server.Direction);
    }

    [Fact]
    public void DeleteIsUnrepresentableAndNonLiteralPathsQuarantine()
    {
        var delete = Lower("Files", "delete", CallArg.Literal("/tmp/x"));
        Assert.Equal(LiftOutcome.Unrepresentable, delete!.Outcome);
        Assert.Equal("delete", delete.UnrepresentablePrimitive);

        var quarantined = Lower("Files", "readString", new CallArg(ArgKind.Identifier, "userPath"));
        Assert.Equal(LiftOutcome.Quarantine, quarantined!.Outcome);
        Assert.Contains("identifier", quarantined.Reason);

        Assert.Null(Lower("Files", "copy", CallArg.Literal("/a"), CallArg.Literal("/b")));
    }
}
