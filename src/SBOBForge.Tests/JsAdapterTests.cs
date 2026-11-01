using System.Linq;
using SBOBForge.Adapters.TypeScript;
using SBOBForge.Parsing;
using Xunit;

namespace SBOBForge.Tests;

public class JsAdapterTests
{
    private static CodeModel Parse(string source) =>
        new TypeScriptSourceParser().Parse("tests/store.test.js", source);

    [Fact]
    public void JestOwnsTheUnconditionalSurfaceWithCasesAndSkips()
    {
        var model = Parse("""
            describe("config store", () => {
              it("loads config", () => {
                loadConfig();
              });

              it.skip("legacy path", () => {
                loadUser("legacy");
              });

              xit("explicitly disabled", () => {
                loadUser("x");
              });

              test.only("pinned focus", () => {
                run();
              });

              test.each(["/var/log/a.log", "/var/log/b.log"])("writes log %s", (p) => {
                fs.appendFileSync(p, "entry");
              });
            });
            """);
        var jest = new JestAdapter();
        Assert.True(jest.CanAdapt(model));
        Assert.Equal("jest", jest.FrameworkId);
        // vitest owns nothing here: no skipIf entries
        Assert.False(new VitestAdapter().CanAdapt(model));

        var entries = jest.ExtractEntries(model);
        // it + it.skip + xit + test.only + test.each (2 case entries) = 6
        Assert.Equal(6, entries.Count);
        Assert.Single(entries, e => e.TestId == "tests/store.test.js::config store > loads config" && e.Enabled);
        Assert.Single(entries, e => e.TestId == "tests/store.test.js::config store > legacy path" && !e.Enabled);
        Assert.Single(entries, e => e.TestId == "tests/store.test.js::config store > explicitly disabled" && !e.Enabled);
        // .only is a runner filter, not a skip: state stays run
        Assert.Single(entries, e => e.TestId == "tests/store.test.js::config store > pinned focus" && e.Enabled);
        // .each case propagation mirrors pytest parametrize: one entry per case literal
        Assert.Single(entries, e => e.TestId == "tests/store.test.js::config store > writes log %s[0]"
            && e.TheoryCaseValues.SequenceEqual(new[] { "/var/log/a.log" }));
        Assert.Single(entries, e => e.TestId == "tests/store.test.js::config store > writes log %s[1]"
            && e.TheoryCaseValues.SequenceEqual(new[] { "/var/log/b.log" }));
    }

    [Fact]
    public void VitestOwnsSkipIfEntriesWithLiteralSemantics()
    {
        var model = Parse("""
            describe("status client", () => {
              it.skipIf(true)("legacy shell", () => {
                shellOut();
              });

              it.skipIf(false)("active branch", () => {
                fetchStatus();
              });

              it.skipIf(process.env.LEGACY)("env branch", () => {
                shellOut();
              });

              it("plain entry", () => {
                fetchStatus();
              });
            });
            """);
        var vitest = new VitestAdapter();
        Assert.True(vitest.CanAdapt(model));
        Assert.Equal("vitest", vitest.FrameworkId);

        var entries = vitest.ExtractEntries(model);
        Assert.Equal(3, entries.Count);
        // skipIf(true) proves a skip → disabled (mirrors unittest semantics)
        Assert.Single(entries, e => e.TestId.EndsWith("legacy shell") && !e.Enabled);
        // skipIf(false) proves nothing is skipped → enabled
        Assert.Single(entries, e => e.TestId.EndsWith("active branch") && e.Enabled);
        // non-literal condition cannot prove a skip → enabled
        Assert.Single(entries, e => e.TestId.EndsWith("env branch") && e.Enabled);

        // the plain entry stays with Jest (ownership split, TDS 7.2)
        var jest = new JestAdapter();
        Assert.True(jest.CanAdapt(model));
        Assert.Single(jest.ExtractEntries(model), e => e.TestId.EndsWith("plain entry"));
    }

    [Fact]
    public void RegistryMergesJestAndVitestInFixedOrder()
    {
        var model = Parse("""
            it("plain entry", () => {
              fetchStatus();
            });

            it.skipIf(true)("conditional entry", () => {
              shellOut();
            });
            """);
        var entries = new JsTestFrameworkRegistry().ExtractEntries(model);
        Assert.Equal(2, entries.Count);
        // jest first (unconditional surface), then vitest (skipIf entries)
        Assert.True(entries[0].Enabled);
        Assert.Equal("tests/store.test.js::plain entry", entries[0].TestId);
        Assert.False(entries[1].Enabled);
        Assert.Equal("tests/store.test.js::conditional entry", entries[1].TestId);
    }
}
