using System.Linq;
using SBOBForge.Core;
using SBOBForge.Parsing;
using Xunit;

namespace SBOBForge.Tests;

public class JavaParserTests
{
    private static CodeModel Parse(string source) =>
        new JavaSourceParser().Parse("src/test/java/Sample.java", source);

    [Fact]
    public void ExtractsPackageImportsTypesMethodsAndAnnotations()
    {
        var model = Parse("""
            package com.example.core;

            import java.nio.file.Files;
            import java.nio.file.Path;
            import org.junit.jupiter.api.Test;

            public class ConfigStoreTests
            {
                @Test
                public void loadsConfig() throws Exception
                {
                    String config = Files.readString(Path.of("/etc/app/config.json"));
                }
            }
            """);
        var type = Assert.Single(model.Types, t => t.Name == "ConfigStoreTests");
        Assert.Equal("com.example.core", type.Namespace);
        Assert.Contains("org.junit.jupiter.api.Test", model.ImportList);
        Assert.Contains("java.nio.file.Files", model.ImportList);

        var method = Assert.Single(type.Methods, m => m.Name == "loadsConfig");
        Assert.Equal("Test", Assert.Single(method.Attributes).Name);
        Assert.Empty(method.Parameters);
        Assert.Equal("com.example.core.ConfigStoreTests.loadsConfig", method.QualifiedName);

        var read = Assert.Single(method.Invocations, i => i.Method == "readString");
        Assert.Equal("Files", read.Receiver);
        var path = Assert.Single(read.Args);
        Assert.Equal(ArgKind.Literal, path.Kind);
        Assert.Equal("/etc/app/config.json", path.Value);
    }

    [Fact]
    public void ParameterizedAnnotationsProduceListValuesAndParameterNamesPropagate()
    {
        var model = Parse("""
            import org.junit.jupiter.params.ParameterizedTest;
            import org.junit.jupiter.params.provider.ValueSource;
            import java.nio.file.Files;
            import java.nio.file.Path;

            public class LogTests
            {
                @ParameterizedTest
                @ValueSource(strings = { "/var/log/app.log", "/var/log/other.log" })
                public void writesLog(String path) throws Exception
                {
                    Files.write(Path.of(path), new byte[] { 1 });
                }
            }
            """);
        var method = Assert.Single(Assert.Single(model.Types).Methods);
        Assert.Equal("ParameterizedTest", method.Attributes[0].Name);
        Assert.Equal("ValueSource", method.Attributes[1].Name);

        var values = Assert.Single(method.Attributes[1].Args);
        Assert.Equal("strings", values.Name);
        Assert.Equal(ArgKind.List, values.Kind);
        Assert.Equal(new[] { "/var/log/app.log", "/var/log/other.log" }, values.ListElements());

        Assert.Equal(new[] { "path" }, method.Parameters);
        var write = Assert.Single(method.Invocations, i => i.Method == "write");
        Assert.Equal("Files", write.Receiver);
        // Path.of(path) with a non-literal single argument propagates the identifier kind
        Assert.Equal(ArgKind.Identifier, write.Args[0].Kind);
        Assert.Equal("path", write.Args[0].Value);
    }

    [Fact]
    public void ConstructionsAndChainedReceivers()
    {
        var model = Parse("""
            import java.net.URL;
            import java.net.ServerSocket;

            public class NetTests
            {
                public void m() throws Exception
                {
                    URL status = new URL("https://api.example.com/status");
                    ServerSocket server = new ServerSocket(8080);
                    ProcessBuilder pb = new ProcessBuilder("git", "status");
                    Runtime.getRuntime().exec("git status");
                    Files.readString("/var/log/" + "app.log");
                }
            }
            """);
        var method = Assert.Single(Assert.Single(model.Types).Methods);

        var url = Assert.Single(method.Invocations, i => i.Method == "URL");
        Assert.Null(url.Receiver);
        Assert.Equal("https://api.example.com/status", url.Args[0].Value);

        var server = Assert.Single(method.Invocations, i => i.Method == "ServerSocket");
        Assert.Equal("8080", server.Args[0].Value);

        var pb = Assert.Single(method.Invocations, i => i.Method == "ProcessBuilder");
        Assert.Equal(new[] { "git", "status" }, pb.Args.Select(a => a.Value));

        // Runtime.getRuntime().exec: receiver chain collapses to the method-like segment
        var exec = Assert.Single(method.Invocations, i => i.Method == "exec");
        Assert.Equal("getRuntime", exec.Receiver);
        Assert.Equal("git status", exec.Args[0].Value);

        // constant-folded string concatenation
        var read = Assert.Single(method.Invocations, i => i.Method == "readString");
        Assert.Equal("/var/log/app.log", read.Args[0].Value);
    }

    [Fact]
    public void UnbalancedBracesAreHardErrors()
    {
        Assert.Throws<ParseHardError>(() => Parse("public class A {"));
    }

    [Fact]
    public void PartiallyLiteralArgvAnchorsLiteralArgv0()
    {
        var model = Parse("""
            import java.util.List;

            public class Repo {
                public void pull(String d) throws Exception {
                    ProcessBuilder pb = new ProcessBuilder(List.of("git", "-C", d, "pull"));
                }
            }
            """);
        var pull = Assert.Single(Assert.Single(model.Types).Methods, m => m.Name == "pull");
        var pb = Assert.Single(pull.Invocations, i => i.Method == "ProcessBuilder");
        var argv = pb.Args[0];
        Assert.Equal(ArgKind.PartialList, argv.Kind);
        Assert.Equal("git", argv.Value);
    }
}
