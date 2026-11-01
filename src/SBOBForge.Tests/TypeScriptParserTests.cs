using System.Linq;
using SBOBForge.Core;
using SBOBForge.Parsing;
using Xunit;

namespace SBOBForge.Tests;

public class TypeScriptParserTests
{
    private static CodeModel Parse(string source) =>
        new TypeScriptSourceParser().Parse("tests/store.test.js", source);

    private static AttributeDecl EntryAttr(MethodDecl method) =>
        Assert.Single(method.Attributes, a => a.Name == "js.entry");

    [Fact]
    public void EntriesCarryTitlePathStateAndCases()
    {
        var model = Parse("""
            describe("config store", () => {
              it("loads config", () => {
                loadConfig();
              });

              it.skip("legacy path", () => {
                loadUser("legacy");
              });

              test.each(["/var/log/a.log", "/var/log/b.log"])("writes log %s", (p) => {
                fs.appendFileSync(p, "entry");
              });
            });
            """);
        var module = Assert.Single(model.Types);
        Assert.Equal("store.test", module.Name);

        // one synthesized method per entry call, name = fullTitle@line
        var loads = Assert.Single(module.Methods, m => m.Name.StartsWith("config store > loads config@"));
        var attr = EntryAttr(loads);
        Assert.Equal("loads config", attr.Args.Single(a => a.IsNamed("title")).Value);
        Assert.Equal("config store > loads config", attr.Args.Single(a => a.IsNamed("full")).Value);
        Assert.Equal("run", attr.Args.Single(a => a.IsNamed("state")).Value);

        // callback params are method parameters (M1 case-value substitution) and the
        // callback body's invocations are scanned
        Assert.Empty(loads.Parameters);
        var call = Assert.Single(loads.Invocations);
        Assert.Equal("loadConfig", call.Method);

        var skip = Assert.Single(module.Methods, m => m.Name.StartsWith("config store > legacy path@"));
        Assert.Equal("skip", EntryAttr(skip).Args.Single(a => a.IsNamed("state")).Value);

        // .each: single-parameter fast path with the case literals on the attribute
        var each = Assert.Single(module.Methods, m => m.Name.StartsWith("config store > writes log %s@"));
        var eachAttr = EntryAttr(each);
        var cases = Assert.Single(eachAttr.Args, a => a.IsNamed("cases"));
        Assert.Equal(new[] { "/var/log/a.log", "/var/log/b.log" }, cases.ListElements());
        Assert.Equal(new[] { "p" }, each.Parameters);
        Assert.Single(each.Invocations, i => i.Method == "appendFileSync");
    }

    [Fact]
    public void DescribeSkipResolvesAtDescribeLevel()
    {
        var model = Parse("""
            describe.skip("disabled suite", () => {
              it("never runs", () => {
                shellOut();
              });
            });

            xdescribe("another", () => {
              it("also never", () => {
                shellOut();
              });
            });
            """);
        var module = Assert.Single(model.Types);
        Assert.Equal(2, module.Methods.Count);
        Assert.All(module.Methods, m =>
            Assert.Equal("skip", EntryAttr(m).Args.Single(a => a.IsNamed("state")).Value));
    }

    [Fact]
    public void SkipIfChainKeepsTitleAndCondition()
    {
        var model = Parse("""
            it.skipIf(true)("legacy shell", () => {
              shellOut();
            });
            """);
        var module = Assert.Single(model.Types);
        var entry = Assert.Single(module.Methods);
        var attr = EntryAttr(entry);
        Assert.Equal("legacy shell", attr.Args.Single(a => a.IsNamed("title")).Value);
        Assert.Equal("skipIf", attr.Args.Single(a => a.IsNamed("state")).Value);
        Assert.Equal("True", attr.Args.Single(a => a.IsNamed("skipIf")).Value);
    }

    [Fact]
    public void HooksAreAttributeLessHelpersAndNestedClosuresParse()
    {
        var model = Parse("""
            describe("suite", () => {
              beforeEach(() => {
                loadConfig();
              });

              it("works with nested objects and arrays", () => {
                const data = { a: [1, 2, { b: "x" }] };
                process(data);
              });
            });
            """);
        var module = Assert.Single(model.Types);
        var hook = Assert.Single(module.Methods, m => m.Name.StartsWith("beforeEach@"));
        Assert.Empty(hook.Attributes);
        Assert.Single(hook.Invocations, i => i.Method == "loadConfig");

        // regression: a describe callback containing object/array literals with
        // nested braces must not desync brace matching
        var entry = Assert.Single(module.Methods, m => m.Name.StartsWith("suite > works with nested objects"));
        Assert.Equal("run", EntryAttr(entry).Args.Single(a => a.IsNamed("state")).Value);
        Assert.Single(entry.Invocations, i => i.Method == "process");
    }

    [Fact]
    public void ModuleFunctionsClassesAndTemplateLiterals()
    {
        var model = Parse("""
            import * as fs from "fs";
            import { exec } from "child_process";

            function loadConfig() {
              return fs.readFileSync("/etc/app/config.json", "utf8");
            }

            export class Store {
              save(entry) {
                fs.appendFileSync(this.path, entry);
              }
            }

            const fetchStatus = () => {
              return fetch("https://api.example.com/status");
            };

            function loadUser(user) {
              return fs.readFileSync(`/etc/app/users/${user}.json`, "utf8");
            }
            """);
        // module pseudo-type + one type per class declaration
        Assert.Equal(2, model.Types.Count);
        Assert.Equal(new[] { "fs", "child_process" }, (model.Imports ?? Array.Empty<string>()).ToArray());

        var module = Assert.Single(model.Types, t => t.Name == "store.test");
        var load = Assert.Single(module.Methods, m => m.Name == "loadConfig");
        var read = Assert.Single(load.Invocations);
        Assert.Equal("fs", read.Receiver);
        Assert.Equal("/etc/app/config.json", read.Args[0].Value);

        var store = Assert.Single(model.Types, t => t.Name == "Store");
        var save = Assert.Single(store.Methods, m => m.Name == "save");
        Assert.Equal(new[] { "entry" }, save.Parameters);

        // const-arrow assignments become module methods
        Assert.Single(module.Methods, m => m.Name == "fetchStatus");

        // template literal with ${…} → FStr (quarantine tier downstream)
        var loadUser = Assert.Single(module.Methods, m => m.Name == "loadUser");
        var template = Assert.Single(loadUser.Invocations).Args[0];
        Assert.Equal(ArgKind.Interpolated, template.Kind);
    }

    [Fact]
    public void UnbalancedBraceIsAHardError()
    {
        Assert.Throws<ParseHardError>(() => Parse("it(\"a\", () => {\n"));
    }
}
