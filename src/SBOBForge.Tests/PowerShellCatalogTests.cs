using System.Linq;
using SBOBForge.Catalog;
using SBOBForge.Core.Model;
using SBOBForge.Parsing;
using Xunit;

namespace SBOBForge.Tests;

public class PowerShellCatalogTests
{
    private static LoweredInvocation? Lower(
        string? receiver, string method, params CallArg[] args) =>
        PowerShellCatalogLowering.Lower(new InvocationDecl(receiver, method, 1, args));

    [Fact]
    public void CatalogLoadsAndHasLockEntryName()
    {
        var catalog = PowerShellCatalogLowering.Catalog;
        Assert.Equal("powershell", catalog.Id);
        Assert.Equal(1, catalog.Version);
        Assert.NotEmpty(catalog.Rules);
        Assert.Equal("powershell-catalog", PowerShellCatalog.LockEntryName);
        Assert.NotEmpty(PowerShellCatalog.ComputeDefaultHash());
    }

    [Fact]
    public void ContentCmdletsLowerToOpens()
    {
        var read = Lower(null, "Get-Content",
            new CallArg(ArgKind.Literal, "/etc/app/config.json", "Path"),
            new CallArg(ArgKind.Identifier, "true", "Raw"));
        Assert.Equal(BehaviorPrimitive.Open, read!.Primitive);
        Assert.Equal(new[] { "O_RDONLY" }, read.Flags);

        var append = Lower(null, "Add-Content",
            new CallArg(ArgKind.Literal, "/var/log/ps-app.log", "Path"),
            new CallArg(ArgKind.Literal, "entry", "Value"));
        Assert.Equal(new[] { "O_WRONLY", "O_CREAT", "O_APPEND" }, append!.Flags);

        var redirect = Lower(null, ">>", CallArg.Literal("/var/log/ps-app.log"));
        Assert.Equal(new[] { "O_WRONLY", "O_CREAT", "O_APPEND" }, redirect!.Flags);
    }

    [Fact]
    public void CallOperatorLiftsProgramAndExactArgv()
    {
        var git = Lower(null, "&", CallArg.Literal("git"), CallArg.Literal("status"));
        Assert.Equal(BehaviorPrimitive.Exec, git!.Primitive);
        Assert.Equal("git", git.Path);
        Assert.Equal(new[] { "status" }, git.Args);
        Assert.True(git.ArgsAllLiteral);

        // dynamic arguments argv0-anchor: program knowable, args are not
        var partial = Lower(null, "&", CallArg.Literal("git"),
            new CallArg(ArgKind.Identifier, "$branch"));
        Assert.Equal("git", partial!.Path);
        Assert.False(partial.ArgsAllLiteral);

        // non-literal program quarantines
        var bad = Lower(null, "&", new CallArg(ArgKind.Identifier, "$cmd"));
        Assert.Equal(LiftOutcome.Quarantine, bad!.Outcome);
    }

    [Fact]
    public void StartProcessNamedParametersAndInvokeExpression()
    {
        var start = Lower(null, "Start-Process",
            new CallArg(ArgKind.Literal, "git", "FilePath"),
            new CallArg(ArgKind.Literal, "status", "ArgumentList"),
            new CallArg(ArgKind.Identifier, "true", "NoNewWindow"));
        Assert.Equal(BehaviorPrimitive.Exec, start!.Primitive);
        Assert.Equal("git", start.Path);
        Assert.Equal(new[] { "status" }, start.Args);

        // missing -FilePath quarantines
        var missing = Lower(null, "Start-Process", new CallArg(ArgKind.Identifier, "true", "NoNewWindow"));
        Assert.Equal(LiftOutcome.Quarantine, missing!.Outcome);

        // Invoke-Expression "git status": constant string lowers by first token
        var shell = Lower(null, "Invoke-Expression", CallArg.Literal("git status"));
        Assert.Equal("git", shell!.Path);
        Assert.Equal(new[] { "status" }, shell.Args);

        // non-constant Invoke-Expression is RCE-shaped and always quarantines
        var rce = Lower(null, "Invoke-Expression", new CallArg(ArgKind.Identifier, "$userInput"));
        Assert.Equal(LiftOutcome.Quarantine, rce!.Outcome);
        Assert.Contains("RCE-shaped", rce.Reason);
    }

    [Fact]
    public void WebRequestLowersToEgressAndTcpClientToEgressHostPort()
    {
        var web = Lower(null, "Invoke-WebRequest",
            new CallArg(ArgKind.Literal, "https://api.example.com/status", "Uri"));
        Assert.Equal(BehaviorPrimitive.Connect, web!.Primitive);
        Assert.Equal("api.example.com", web.DnsName);
        Assert.Equal(443, web.Port);

        var rest = Lower(null, "Invoke-RestMethod",
            new CallArg(ArgKind.Literal, "http://insecure.internal/health", "Uri"));
        Assert.Equal(80, rest!.Port);

        var tcp = Lower(null, "TcpClient", CallArg.Literal("db.internal"), CallArg.Literal("5432"));
        Assert.Equal(BehaviorPrimitive.Connect, tcp!.Primitive);
        Assert.Equal("db.internal", tcp.DnsName);
        Assert.Equal(5432, tcp.Port);
    }

    [Fact]
    public void RemoveItemIsUnrepresentableDelete()
    {
        var remove = Lower(null, "Remove-Item",
            new CallArg(ArgKind.Literal, "%PackageRoot%/cache/last.json", "Path"));
        Assert.Equal(LiftOutcome.Unrepresentable, remove!.Outcome);
        Assert.Equal("delete", remove.UnrepresentablePrimitive);
    }
}
