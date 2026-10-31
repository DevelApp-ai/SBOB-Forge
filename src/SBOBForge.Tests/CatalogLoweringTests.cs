using System;
using SBOBForge.Catalog;
using SBOBForge.Core.Model;
using SBOBForge.Parsing;
using Xunit;

namespace SBOBForge.Tests;

public class CatalogLoweringTests
{
    private static InvocationDecl Inv(string? receiver, string method, params CallArg[] args) =>
        new(receiver, method, 10, args);

    [Fact]
    public void FileReadAllTextLiteralOpensReadOnly()
    {
        var lifted = DotNetCatalogLowering.Lower(Inv("File", "ReadAllText", CallArg.Literal("/etc/app/config.json")));
        Assert.NotNull(lifted);
        Assert.Equal(LiftOutcome.Event, lifted!.Outcome);
        Assert.Equal(BehaviorPrimitive.Open, lifted.Primitive);
        Assert.Equal("/etc/app/config.json", lifted.Path);
        Assert.Equal(new[] { "O_RDONLY" }, lifted.Flags);
    }

    [Fact]
    public void NonLiteralPathIsQuarantinedNotGuessed()
    {
        var lifted = DotNetCatalogLowering.Lower(Inv("File", "ReadAllText",
            new CallArg(ArgKind.Interpolated, "$\"/etc/{user}\"")));
        Assert.NotNull(lifted);
        Assert.Equal(LiftOutcome.Quarantine, lifted!.Outcome);
        Assert.Contains("interpolated", lifted.Reason);
    }

    [Theory]
    [InlineData("WriteAllText", new[] { "O_WRONLY", "O_CREAT", "O_TRUNC" })]
    [InlineData("AppendAllText", new[] { "O_WRONLY", "O_CREAT", "O_APPEND" })]
    public void WriteFamilyMapsToWriteFlags(string method, string[] expectedFlags)
    {
        var lifted = DotNetCatalogLowering.Lower(Inv("File", method, CallArg.Literal("/tmp/x"), CallArg.Literal("data")));
        Assert.Equal(expectedFlags, lifted!.Flags);
    }

    [Fact]
    public void FileOpenMapsFileModeMembers()
    {
        var create = DotNetCatalogLowering.Lower(Inv("File", "Open",
            CallArg.Literal("/tmp/x"), new CallArg(ArgKind.Identifier, "FileMode.Create")));
        Assert.Equal(new[] { "O_WRONLY", "O_CREAT", "O_TRUNC" }, create!.Flags);

        var append = DotNetCatalogLowering.Lower(Inv("File", "Open",
            CallArg.Literal("/tmp/x"), new CallArg(ArgKind.Identifier, "FileMode.Append")));
        Assert.Equal(new[] { "O_WRONLY", "O_CREAT", "O_APPEND" }, append!.Flags);

        var dynamic = DotNetCatalogLowering.Lower(Inv("File", "Open",
            CallArg.Literal("/tmp/x"), new CallArg(ArgKind.Identifier, "mode")));
        Assert.Equal(LiftOutcome.Quarantine, dynamic!.Outcome);
    }

    [Fact]
    public void AsyncFileFamilyLiftsLikeSyncCounterparts()
    {
        // Dogfood finding (DevelApp.Minotaur suite): the File.*Async family is
        // common in real SUTs and must lower identically to the sync rules.
        var read = DotNetCatalogLowering.Lower(Inv("File", "ReadAllTextAsync", CallArg.Literal("/etc/app/config.json")));
        Assert.Equal(BehaviorPrimitive.Open, read!.Primitive);
        Assert.Equal("/etc/app/config.json", read.Path);
        Assert.Equal(new[] { "O_RDONLY" }, read.Flags);

        var write = DotNetCatalogLowering.Lower(Inv("File", "WriteAllTextAsync",
            CallArg.Literal("/tmp/sample.sscript"), CallArg.Literal("var x = 1;")));
        Assert.Equal(new[] { "O_WRONLY", "O_CREAT", "O_TRUNC" }, write!.Flags);

        var append = DotNetCatalogLowering.Lower(Inv("File", "AppendAllTextAsync",
            CallArg.Literal("/var/log/app.log"), CallArg.Literal("entry")));
        Assert.Equal(new[] { "O_WRONLY", "O_CREAT", "O_APPEND" }, append!.Flags);

        var lines = DotNetCatalogLowering.Lower(Inv("File", "ReadAllLinesAsync", CallArg.Literal("/etc/hosts")));
        Assert.Equal(new[] { "O_RDONLY" }, lines!.Flags);
    }

    [Fact]
    public void FileDeleteIsUnrepresentableInSpecV004()
    {
        var lifted = DotNetCatalogLowering.Lower(Inv("File", "Delete", CallArg.Literal("/tmp/x")));
        Assert.Equal(LiftOutcome.Unrepresentable, lifted!.Outcome);
        Assert.Equal("delete", lifted.UnrepresentablePrimitive);
    }

    [Fact]
    public void DirectoryCreateDirectoryOpensDirectoryFlags()
    {
        var lifted = DotNetCatalogLowering.Lower(Inv("Directory", "CreateDirectory", CallArg.Literal("/app")));
        Assert.Equal(new[] { "O_DIRECTORY", "O_CREAT" }, lifted!.Flags);
    }

    [Fact]
    public void ProcessStartLiftsLiteralExecTargets()
    {
        var lifted = DotNetCatalogLowering.Lower(Inv("Process", "Start",
            CallArg.Literal("git"), CallArg.Literal("status")));
        Assert.Equal(BehaviorPrimitive.Exec, lifted!.Primitive);
        Assert.Equal("git", lifted.Path);
        Assert.Equal(new[] { "status" }, lifted.Args);
        Assert.True(lifted.ArgsAllLiteral);

        var identifier = DotNetCatalogLowering.Lower(Inv("Process", "Start",
            new CallArg(ArgKind.Identifier, "toolPath")));
        Assert.Equal(LiftOutcome.Quarantine, identifier!.Outcome);
    }

    [Fact]
    public void HttpVerbsLiftToEgressWithSchemeDefaultPorts()
    {
        var https = DotNetCatalogLowering.Lower(Inv(null, "GetStringAsync",
            CallArg.Literal("https://API.Example.com/status")));
        Assert.Equal(BehaviorPrimitive.Connect, https!.Primitive);
        Assert.Equal(NetDirection.Egress, https.Direction);
        Assert.Equal("api.example.com", https.DnsName);
        Assert.Equal(443, https.Port);

        var http = DotNetCatalogLowering.Lower(Inv("client", "GetAsync",
            CallArg.Literal("http://api.example.com")));
        Assert.Equal(80, http!.Port);

        var explicitPort = DotNetCatalogLowering.Lower(Inv("client", "PostAsync",
            CallArg.Literal("https://api.example.com:8081/x")));
        Assert.Equal(8081, explicitPort!.Port);

        var relative = DotNetCatalogLowering.Lower(Inv("client", "GetAsync",
            CallArg.Literal("/api/status")));
        Assert.Equal(LiftOutcome.Quarantine, relative!.Outcome);
    }

    [Fact]
    public void TcpListenerConstructionLiftsLiteralPortToIngress()
    {
        var lifted = DotNetCatalogLowering.Lower(Inv(null, "TcpListener",
            new CallArg(ArgKind.Identifier, "System.Net.IPAddress.Any"), CallArg.Literal("8080")));
        Assert.Equal(BehaviorPrimitive.Listen, lifted!.Primitive);
        Assert.Equal(NetDirection.Ingress, lifted.Direction);
        Assert.Equal(8080, lifted.Port);

        var nonLiteral = DotNetCatalogLowering.Lower(Inv(null, "TcpListener",
            new CallArg(ArgKind.Identifier, "System.Net.IPAddress.Any"),
            new CallArg(ArgKind.Identifier, "port")));
        Assert.Equal(LiftOutcome.Quarantine, nonLiteral!.Outcome);
    }

    [Fact]
    public void SubstitutionResolvesIdentifierArgs()
    {
        var lifted = DotNetCatalogLowering.Lower(
            Inv("File", "WriteAllText",
                new CallArg(ArgKind.Identifier, "path"), CallArg.Literal("data")),
            arg => arg.Kind == ArgKind.Identifier && arg.Value == "path"
                ? CallArg.Literal("/var/log/app.log")
                : null);
        Assert.Equal(LiftOutcome.Event, lifted!.Outcome);
        Assert.Equal("/var/log/app.log", lifted.Path);
    }

    [Fact]
    public void UnknownApisDoNotMatchAnyRule()
    {
        Assert.Null(DotNetCatalogLowering.Lower(Inv("Assert", "NotNull", new CallArg(ArgKind.Identifier, "x"))));
        Assert.Null(DotNetCatalogLowering.Lower(Inv("listener", "Start")));
    }
}
