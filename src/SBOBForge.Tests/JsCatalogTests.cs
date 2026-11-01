using System.Linq;
using SBOBForge.Catalog;
using SBOBForge.Core.Model;
using SBOBForge.Parsing;
using Xunit;

namespace SBOBForge.Tests;

public class JsCatalogTests
{
    private static LoweredInvocation? Lower(
        string? receiver, string method, params CallArg[] args) =>
        JsCatalogLowering.Lower(new InvocationDecl(receiver, method, 1, args));

    [Fact]
    public void CatalogLoadsAndHasLockEntryName()
    {
        var catalog = JsCatalogLowering.Catalog;
        Assert.Equal("js", catalog.Id);
        Assert.Equal(1, catalog.Version);
        Assert.NotEmpty(catalog.Rules);
        Assert.Equal("js-catalog", JsCatalog.LockEntryName);
        Assert.NotEmpty(JsCatalog.ComputeDefaultHash());
    }

    [Fact]
    public void BareVsQualifiedChildProcessFormsLower()
    {
        // destructured `const { execFile } = require("child_process")` → bare call
        var bare = Lower(null, "execFile", CallArg.Literal("git"),
            new CallArg(ArgKind.List, "status"));
        Assert.Equal(BehaviorPrimitive.Exec, bare!.Primitive);
        Assert.Equal("git", bare.Path);
        Assert.Equal(new[] { "status" }, bare.Args);

        var qualified = Lower("child_process", "spawn", CallArg.Literal("ffmpeg"),
            new CallArg(ArgKind.List, "-i\u0001in.mp4"));
        Assert.Equal("ffmpeg", qualified!.Path);
        Assert.Equal(new[] { "-i", "in.mp4" }, qualified.Args);

        // shell forms lower to /bin/sh -c
        var shell = Lower(null, "exec", CallArg.Literal("git status"));
        Assert.Equal("/bin/sh", shell!.Path);
        Assert.Equal(new[] { "-c", "git status" }, shell.Args);

        // RegExp.exec and other receiver-qualified 'exec' must NOT match the bare rule
        var regex = Lower("re", "exec", CallArg.Literal("[a-z]"));
        Assert.Null(regex);
        // and must not fall into the child_process-qualified rule either
        var other = Lower("lodash", "exec", CallArg.Literal("fn"));
        Assert.Null(other);

        // dynamic argv list: argv0-anchored (program knowable, args not)
        var dynamic = Lower(null, "spawn", CallArg.Literal("ffmpeg"),
            new CallArg(ArgKind.Identifier, "args"));
        Assert.Equal("ffmpeg", dynamic!.Path);
        Assert.False(dynamic.ArgsAllLiteral);
    }

    [Fact]
    public void FsOpenFormsLowerToSpecFlagSets()
    {
        var read = Lower("fs", "readFileSync", CallArg.Literal("/etc/app/config.json"));
        Assert.Equal(new[] { "O_RDONLY" }, read!.Flags);

        var write = Lower("fs", "writeFileSync", CallArg.Literal("/var/log/app.log"));
        Assert.Equal(new[] { "O_WRONLY", "O_CREAT", "O_TRUNC" }, write!.Flags);

        var append = Lower("fs", "appendFileSync", CallArg.Literal("/var/log/app.log"));
        Assert.Equal(new[] { "O_WRONLY", "O_CREAT", "O_APPEND" }, append!.Flags);

        // mode strings: openSync(path, "a") → append flags
        var mode = Lower("fs", "openSync", CallArg.Literal("/var/log/app.log"), CallArg.Literal("a"));
        Assert.Equal(new[] { "O_WRONLY", "O_CREAT", "O_APPEND" }, mode!.Flags);

        var readOnly = Lower("fs", "openSync", CallArg.Literal("/etc/passwd"), CallArg.Literal("r"));
        Assert.Equal(new[] { "O_RDONLY" }, readOnly!.Flags);

        // template-literal path quarantines
        var template = Lower("fs", "readFileSync", new CallArg(ArgKind.Interpolated, "/etc/users/${u}"));
        Assert.Equal(LiftOutcome.Quarantine, template!.Outcome);
    }

    [Fact]
    public void DeletesAreUnrepresentable()
    {
        foreach (var (receiver, method) in new[]
                 {
                     (null, "unlinkSync"), ("fs", "rmSync"), (null, "rmdir"), ("fs", "unlink")
                 })
        {
            var lowered = Lower(receiver, method, CallArg.Literal("/tmp/cache"));
            Assert.NotNull(lowered);
            Assert.Equal("delete", lowered!.UnrepresentablePrimitive);
        }
    }

    [Fact]
    public void NetworkFormsLower()
    {
        // global fetch (bare) → egress
        var fetch = Lower(null, "fetch", CallArg.Literal("https://api.example.com/status"));
        Assert.Equal(NetDirection.Egress, fetch!.Direction);
        Assert.Equal("api.example.com", fetch.DnsName);
        Assert.Equal(443, fetch.Port);

        // qualified https.get → egress
        var get = Lower("https", "get", CallArg.Literal("https://api.example.com/status"));
        Assert.Equal("api.example.com", get!.DnsName);

        // server.listen(port) — any receiver (receiver: null rule) → ingress
        var listen = Lower("server", "listen", CallArg.Literal("8080"));
        Assert.Equal(NetDirection.Ingress, listen!.Direction);
        Assert.Equal(8080, listen.Port);

        // non-literal port quarantines
        var dyn = Lower("server", "listen", new CallArg(ArgKind.Identifier, "port"));
        Assert.Equal(LiftOutcome.Quarantine, dyn!.Outcome);
    }
}
