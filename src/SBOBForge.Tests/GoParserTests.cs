using System.Linq;
using SBOBForge.Core;
using SBOBForge.Parsing;
using Xunit;

namespace SBOBForge.Tests;

public class GoParserTests
{
    private static CodeModel Parse(string source) =>
        new GoSourceParser().Parse("sut/go-core/configstore.go", source);

    [Fact]
    public void PackagePseudoTypeImportsAndFuncs()
    {
        var model = Parse("""
            package core

            import (
            	"net/http"
            	"os"
            	"os/exec"
            )

            func LoadConfig() ([]byte, error) {
            	return os.ReadFile("/etc/app/config.json")
            }

            func (s *Store) Save(data []byte) error {
            	return nil
            }
            """);
        // all top-level funcs are methods of one package pseudo-type
        var pkg = Assert.Single(model.Types);
        Assert.Equal("core", pkg.Name);
        Assert.Equal(2, pkg.Methods.Count);

        var load = Assert.Single(pkg.Methods, m => m.Name == "LoadConfig");
        Assert.Equal("core.LoadConfig", load.QualifiedName);
        var read = Assert.Single(load.Invocations);
        Assert.Equal("os", read.Receiver);
        Assert.Equal("ReadFile", read.Method);
        Assert.Equal("/etc/app/config.json", read.Args[0].Value);

        // receiver funcs share the package pseudo-type (methods on a type are not modeled in v1)
        Assert.Single(pkg.Methods, m => m.Name == "Save");
        Assert.Equal(new[] { "net/http", "os", "os/exec" }, (model.Imports ?? Array.Empty<string>()).ToArray());
    }

    [Fact]
    public void ParamNamesAndTestConventionReceivers()
    {
        var model = Parse("""
            package core

            import "testing"

            func TestLegacyPath(t *testing.T) {
            	t.Skip("legacy layout")
            }

            func LoadUser(user string) ([]byte, error) {
            	return os.ReadFile("/etc/app/users/" + user + ".json")
            }
            """);
        var pkg = Assert.Single(model.Types);

        var test = Assert.Single(pkg.Methods, m => m.Name == "TestLegacyPath");
        Assert.Equal(new[] { "t" }, test.Parameters);
        var skip = Assert.Single(test.Invocations, i => i.Method == "Skip");
        Assert.Equal("t", skip.Receiver);
        Assert.Equal("legacy layout", skip.Args[0].Value);

        var load = Assert.Single(pkg.Methods, m => m.Name == "LoadUser");
        Assert.Equal(new[] { "user" }, load.Parameters);
    }

    [Fact]
    public void ArgKindsFlagMasksConcatAndCompositeLiterals()
    {
        var model = Parse("""
            package core

            func AppendLog(p string) error {
            	f, _ := os.OpenFile("/var/log/go-app.log", os.O_WRONLY|os.O_CREATE|os.O_APPEND, 0o644)
            	_ = f
            	return nil
            }

            func Run(argv []string) error {
            	return exec.Command("git", "status").Run()
            }

            func Raw() {
            	os.OpenFile("/etc/" + "app" + "/x.cfg", os.O_RDONLY, 0)
            	exec.Command(cmd)
            }
            """);
        var pkg = Assert.Single(model.Types);

        // os.O_* mask → single canonical flag-mask identifier
        var appendLog = Assert.Single(pkg.Methods, m => m.Name == "AppendLog");
        var openFile = Assert.Single(appendLog.Invocations, i => i.Method == "OpenFile");
        Assert.Equal("O_WRONLY|O_CREATE|O_APPEND", openFile.Args[1].Value);
        Assert.Equal(ArgKind.Identifier, openFile.Args[1].Kind);

        // exec.Command("git", "status"): two literal argv entries
        var run = Assert.Single(pkg.Methods, m => m.Name == "Run");
        var command = Assert.Single(run.Invocations, i => i.Method == "Command");
        Assert.Equal("exec", command.Receiver);
        Assert.Equal("git", command.Args[0].Value);
        Assert.Equal("status", command.Args[1].Value);

        // literal string concat folds to one literal; non-literal argv stays an expression
        var raw = Assert.Single(pkg.Methods, m => m.Name == "Raw");
        var openCfg = Assert.Single(raw.Invocations, i => i.Method == "OpenFile");
        Assert.Equal("/etc/app/x.cfg", openCfg.Args[0].Value);
        var dynamic = Assert.Single(raw.Invocations, i => i.Method == "Command");
        Assert.Equal(ArgKind.Identifier, dynamic.Args[0].Kind);
        Assert.Equal("cmd", dynamic.Args[0].Value);
    }

    [Fact]
    public void UnbalancedBraceIsAHardError()
    {
        Assert.Throws<ParseHardError>(() => Parse("package core\n\nfunc A() {\n"));
    }
}
