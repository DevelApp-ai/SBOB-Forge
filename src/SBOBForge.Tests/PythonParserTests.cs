using System.Linq;
using SBOBForge.Core;
using SBOBForge.Parsing;
using Xunit;

namespace SBOBForge.Tests;

public class PythonParserTests
{
    private static CodeModel Parse(string source) =>
        new PythonSourceParser().Parse("tests/test_core.py", source);

    [Fact]
    public void ModuleFunctionsDecoratorsAndCallScanning()
    {
        var model = Parse("""
            import subprocess
            import pytest

            def run_tool():
                subprocess.run(["git", "status"])

            @pytest.mark.skip
            def test_skipped():
                open("/etc/app/skip.txt")

            @pytest.mark.parametrize("path", ["/var/log/a.log", "/var/log/b.log"])
            def test_write(path):
                with open(path, "a") as f:
                    f.write("data")
            """);
        // module functions are grouped under a pseudo-type named after the module stem
        var module = Assert.Single(model.Types);
        Assert.Equal("test_core", module.Name);
        Assert.Equal(3, module.Methods.Count);

        var run = Assert.Single(module.Methods, m => m.Name == "run_tool");
        var subprocessCall = Assert.Single(run.Invocations);
        Assert.Equal("subprocess", subprocessCall.Receiver);
        Assert.Equal("run", subprocessCall.Method);
        Assert.Equal(ArgKind.List, subprocessCall.Args[0].Kind);
        Assert.Equal(new[] { "git", "status" }, subprocessCall.Args[0].ListElements());

        var skipped = Assert.Single(module.Methods, m => m.Name == "test_skipped");
        Assert.Equal("pytest.mark.skip", Assert.Single(skipped.Attributes).Name);

        var write = Assert.Single(module.Methods, m => m.Name == "test_write");
        var parametrize = Assert.Single(write.Attributes);
        Assert.Equal("pytest.mark.parametrize", parametrize.Name);
        Assert.Equal("path", parametrize.Args[0].Value);
        Assert.Equal(new[] { "/var/log/a.log", "/var/log/b.log" }, parametrize.Args[1].ListElements());
        Assert.Equal(new[] { "path" }, write.Parameters);

        var openCall = Assert.Single(write.Invocations, i => i.Method == "open");
        Assert.Null(openCall.Receiver);
        Assert.Equal(ArgKind.Identifier, openCall.Args[0].Kind);
        Assert.Equal("path", openCall.Args[0].Value);
        Assert.Equal("a", openCall.Args[1].Value);
    }

    [Fact]
    public void ClassesBaseTypesAndDottedReceivers()
    {
        var model = Parse("""
            import unittest
            import urllib.request

            class StatusClientTests(unittest.TestCase):
                @unittest.skipIf(False, "not ready")
                def test_fetch(self):
                    urllib.request.urlopen("https://api.example.com/status")

            class PlainTests:
                def helper(self):
                    pass
            """);
        Assert.Equal(2, model.Types.Count);

        var testCase = Assert.Single(model.Types, t => t.Name == "StatusClientTests");
        Assert.Equal(new[] { "unittest.TestCase" }, testCase.BaseTypeList);

        var fetch = Assert.Single(testCase.Methods);
        Assert.Equal("unittest.skipIf", Assert.Single(fetch.Attributes).Name);
        var skipIfArg = Assert.Single(fetch.Attributes[0].Args, a => a.Value == "not ready");
        Assert.Equal("not ready", skipIfArg.Value);

        var urlopen = Assert.Single(fetch.Invocations);
        Assert.Equal("urllib.request", urlopen.Receiver);
        Assert.Equal("https://api.example.com/status", urlopen.Args[0].Value);

        var plain = Assert.Single(model.Types, t => t.Name == "PlainTests");
        Assert.Empty(plain.BaseTypeList);
        Assert.Single(plain.Methods, m => m.Name == "helper");
    }

    [Fact]
    public void KeywordArgumentsAndAsyncDefs()
    {
        var model = Parse("""
            def shell_out():
                subprocess.run("git status", shell=True)
                os.system("ls /tmp")

            async def fetch_async():
                urllib.request.urlopen("https://api.example.com")
            """);
        var module = Assert.Single(model.Types);
        var shell = Assert.Single(module.Methods, m => m.Name == "shell_out");
        var run = Assert.Single(shell.Invocations, i => i.Method == "run");
        Assert.Equal("subprocess", run.Receiver);
        Assert.Equal("git status", run.Args[0].Value);
        Assert.True(run.Args[1].IsNamed("shell"));
        Assert.Equal("True", run.Args[1].Value);

        var system = Assert.Single(shell.Invocations, i => i.Method == "system");
        Assert.Equal("os", system.Receiver);

        var asyncMethod = Assert.Single(module.Methods, m => m.Name == "fetch_async");
        Assert.Single(asyncMethod.Invocations, i => i.Method == "urlopen");
    }

    [Fact]
    public void BackslashLineContinuationsStayOneLogicalLine()
    {
        // Found dogfooding storytime-audiobook-studio: the continuation
        // line's deeper indent must not create a phantom INDENT, and the
        // dedent back to the suite level must not be an unindent error.
        var model = Parse("""
            import os


            def cover_check(cover):
                ok = cover is not None and \\
                    str(cover).endswith(".jpg")
                if ok and \\
                        os.path.exists(str(cover)):
                    os.remove(str(cover))
                return ok
            """);
        var module = Assert.Single(model.Types);
        var check = Assert.Single(module.Methods, m => m.Name == "cover_check");
        // the whole construct parsed (no phantom INDENT / unindent error) and
        // the calls on both continuation lines are attributed to the method
        Assert.Contains(check.Invocations, i => i.Method == "endswith");
        var remove = Assert.Single(check.Invocations, i => i.Method == "remove");
        Assert.Equal("os", remove.Receiver);
        Assert.True(check.Invocations.All(i => i.Line >= 4 && i.Line <= 10));
    }

    [Fact]
    public void RawStringEscapedQuoteDoesNotTerminate()
    {
        // Found dogfooding storytime-audiobook-studio: in a raw string the
        // backslash stays in the value, but still escapes the quote for
        // tokenization — r"...[\"']..." must not close at the \".
        var model = Parse("""
            import re


            def build():
                quote = re.compile(r"[\"'](.+?)[\"']", re.S)
                digits = re.compile(r"\d+")
                return quote, digits
            """);
        var module = Assert.Single(model.Types);
        var build = Assert.Single(module.Methods, m => m.Name == "build");
        Assert.Equal(2, build.Invocations.Count);
        Assert.All(build.Invocations, i => Assert.Equal("re", i.Receiver));
        // raw value keeps backslashes verbatim, including the escaped quote
        var first = build.Invocations[0].Args[0];
        Assert.Equal(ArgKind.Literal, first.Kind);
        Assert.Equal("[\\\"'](.+?)[\\\"']", first.Value);
    }

    [Fact]
    public void MixedTabSpaceIndentationIsAHardError()
    {
        Assert.Throws<ParseHardError>(() => Parse("def a():\n \tpass\n"));
    }

    [Fact]
    public void PartiallyLiteralArgvAnchorsLiteralArgv0()
    {
        var model = Parse("""
            import subprocess


            def pull(d):
                subprocess.run(["git", "-C", str(d), "pull", "--ff-only"])
                subprocess.run([cmd, "-C", str(d)])
            """);
        var pull = Assert.Single(Assert.Single(model.Types).Methods, m => m.Name == "pull");
        var runs = pull.Invocations.Where(i => i.Method == "run").ToList();
        Assert.Equal(2, runs.Count);
        // literal argv0 + dynamic later args: anchored (TDS 5.2 argv0-anchoring)
        Assert.Equal(ArgKind.PartialList, runs[0].Args[0].Kind);
        Assert.Equal("git", runs[0].Args[0].Value);
        // dynamic argv0 cannot anchor: stays an unresolvable expression
        Assert.Equal(ArgKind.Expression, runs[1].Args[0].Kind);
    }

    [Fact]
    public void BadDedentIsAHardError()
    {
        Assert.Throws<ParseHardError>(() => Parse("def a():\n        x = 1\n    y = 2\n"));
    }
}
