using System.Linq;
using SBOBForge.Core;
using SBOBForge.Parsing;
using Xunit;

namespace SBOBForge.Tests;

public class RoslynParserTests
{
    private static CodeModel Parse(string source) =>
        new RoslynCSharpParser().Parse("src/Sample.cs", source);

    [Fact]
    public void ExtractsTypesMethodsAttributesAndParameters()
    {
        var model = Parse("""
            using Xunit;
            namespace Ns;

            public sealed class T
            {
                [Fact]
                public void Simple() { }

                [Theory]
                [InlineData("a")]
                public void Theory(string p) { }
            }
            """);
        var type = Assert.Single(model.Types, t => t.Name == "T");
        var simple = Assert.Single(type.Methods, m => m.Name == "Simple");
        Assert.Equal("Fact", Assert.Single(simple.Attributes).Name);
        Assert.Empty(simple.Parameters);

        var theory = Assert.Single(type.Methods, m => m.Name == "Theory");
        Assert.Equal("Theory", theory.Attributes[0].Name);
        var inline = theory.Attributes[1];
        Assert.Equal("InlineData", inline.Name);
        Assert.Equal("a", Assert.Single(inline.Args).Value);
        Assert.Equal("p", Assert.Single(theory.Parameters));
        Assert.Equal("Ns.T.Theory", theory.QualifiedName);
    }

    [Fact]
    public void NamedSkipArgumentIsVisible()
    {
        var model = Parse("""
            public sealed class T
            {
                [Fact(Skip = "flaky")]
                public void Skipped() { }
            }
            """);
        var method = Assert.Single(model.AllMethods);
        var arg = Assert.Single(Assert.Single(method.Attributes).Args);
        Assert.True(arg.IsNamed("Skip"));
        Assert.Equal("flaky", arg.Value);
    }

    [Fact]
    public void ClassifiesArgumentKinds()
    {
        var model = Parse("""
            using System.IO;
            public sealed class T
            {
                public void M(string user)
                {
                    File.ReadAllText("/etc/app/config.json");
                    File.ReadAllText($"/users/{user}.json");
                    File.ReadAllText(user);
                    File.ReadAllText("a" + "/b" + ".json");
                    File.ReadAllText(ReadName());
                }
                private string ReadName() => "x";
            }
            """);
        var method = Assert.Single(model.AllMethods, m => m.Name == "M");
        var kinds = method.Invocations
            .Where(i => i.Method == "ReadAllText")
            .Select(i => i.Args[0].Kind)
            .ToArray();
        Assert.Equal(new[]
        {
            ArgKind.Literal,       // "/etc/app/config.json"
            ArgKind.Interpolated,  // $"/users/{user}.json"
            ArgKind.Identifier,    // user
            ArgKind.Literal,       // constant-folded "a/b.json"
            ArgKind.Expression     // ReadName()
        }, kinds);
        var folded = method.Invocations[3].Args[0];
        Assert.Equal("a/b.json", folded.Value);
    }

    [Fact]
    public void UnwrapsReceiversAndCollectsConstructions()
    {
        var model = Parse("""
            using System.Net;
            public sealed class T
            {
                public void M()
                {
                    System.Net.Http.HttpClient h = null!;
                    _ = h.GetStringAsync("https://x.example.com");
                    var l = new TcpListener(System.Net.IPAddress.Any, 8080);
                    l.Start();
                }
            }
            """);
        var method = Assert.Single(model.AllMethods);
        var get = Assert.Single(method.Invocations, i => i.Method == "GetStringAsync");
        Assert.Equal("h", get.Receiver);
        var listener = Assert.Single(method.Invocations, i => i.Method == "TcpListener");
        Assert.Null(listener.Receiver);
        Assert.Equal("8080", listener.Args[1].Value);
    }

    [Fact]
    public void ParseErrorsAreHardErrors()
    {
        Assert.Throws<ParseHardError>(() => Parse("public sealed class"));
    }
}
