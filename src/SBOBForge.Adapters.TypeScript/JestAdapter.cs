using System;
using System.Collections.Generic;
using System.Linq;
using SBOBForge.Adapters;
using SBOBForge.Parsing;

namespace SBOBForge.Adapters.TypeScript;

/// <summary>
/// Shared extraction for the JS/TS entry surface (TDS 7.2, matrix rows
/// JavaScript/TypeScript): the parser synthesizes one "js.entry"-attributed
/// method per it/test call, carrying the title, full describe path, resolved
/// state, and optional .each case literals. The Jest adapter owns the
/// unconditional surface (it/test, .skip, xit/xdescribe, test.each case
/// propagation); Vitest owns only the conditional .skipIf entries (mirrors
/// the pytest/unittest ownership split of M2).
/// </summary>
internal static class JsEntrySurface
{
    public const string AttributeName = "js.entry";

    internal sealed record Entry(
        string Title, string FullTitle, string State, string SkipIfValue, IReadOnlyList<string> Cases,
        MethodDecl Method, CodeModel Model);

    internal static IEnumerable<Entry> Of(CodeModel model, Func<string, bool> stateFilter)
    {
        foreach (var method in model.AllMethods)
        {
            var attr = method.Attributes.FirstOrDefault(a => a.Name == AttributeName);
            if (attr is null) continue;
            var state = attr.Args.FirstOrDefault(a => a.IsNamed("state"))?.Value ?? "run";
            if (!stateFilter(state)) continue;
            var title = attr.Args.FirstOrDefault(a => a.IsNamed("title"))?.Value ?? "";
            var full = attr.Args.FirstOrDefault(a => a.IsNamed("full"))?.Value ?? title;
            var skipIf = attr.Args.FirstOrDefault(a => a.IsNamed("skipIf"))?.Value ?? "";
            var casesArg = attr.Args.FirstOrDefault(a => a.IsNamed("cases"));
            var cases = casesArg is not null ? casesArg.ListElements() : Array.Empty<string>();
            yield return new Entry(title, full, state, skipIf, cases, method, model);
        }
    }

    internal static void Add(List<TestEntry> entries, Entry e)
    {
        var testId = $"{e.Model.RelativePath}::{e.FullTitle}";
        if (e.Cases.Count > 0)
        {
            var caseIndex = 0;
            foreach (var value in e.Cases)
            {
                entries.Add(new TestEntry(
                    testId + $"[{caseIndex}]", e.Model.RelativePath, e.Method.Line,
                    Enabled(e), e.Method.Name, new[] { value }));
                caseIndex++;
            }
            return;
        }
        entries.Add(new TestEntry(
            testId, e.Model.RelativePath, e.Method.Line, Enabled(e), e.Method.Name, Array.Empty<string>()));
    }

    /// <summary>skip → disabled; skipIf(true-literal) → disabled; skipIf(false/non-literal) → enabled (cannot prove a skip; mirrors unittest skipIf semantics).</summary>
    private static bool Enabled(Entry e) =>
        e.State != "skip" && !(e.State == "skipIf" && e.SkipIfValue == "True");
}

/// <summary>Jest adapter (TDS 7.2): the unconditional it/test surface with .skip, xit/xdescribe and .each case propagation.</summary>
public sealed class JestAdapter : ITestFrameworkAdapter
{
    public string FrameworkId => "jest";

    public bool CanAdapt(CodeModel model) =>
        JsEntrySurface.Of(model, state => state is "run" or "skip").Any();

    public IReadOnlyList<TestEntry> ExtractEntries(CodeModel model)
    {
        var entries = new List<TestEntry>();
        foreach (var e in JsEntrySurface.Of(model, state => state is "run" or "skip"))
            JsEntrySurface.Add(entries, e);
        return entries;
    }
}

/// <summary>Vitest adapter (TDS 7.2): owns the conditional .skipIf entries only; the shared it/test surface stays with the Jest adapter.</summary>
public sealed class VitestAdapter : ITestFrameworkAdapter
{
    public string FrameworkId => "vitest";

    public bool CanAdapt(CodeModel model) =>
        JsEntrySurface.Of(model, state => state == "skipIf").Any();

    public IReadOnlyList<TestEntry> ExtractEntries(CodeModel model)
    {
        var entries = new List<TestEntry>();
        foreach (var e in JsEntrySurface.Of(model, state => state == "skipIf"))
            JsEntrySurface.Add(entries, e);
        return entries;
    }
}
