using System;
using System.Collections.Generic;
using System.Linq;
using SBOBForge.Adapters;
using SBOBForge.Parsing;

namespace SBOBForge.Adapters.Cxx;

/// <summary>
/// GoogleTest adapter (TDS 7.2, framework matrix row C/C++). Entry cue: the
/// `cxx.entry` attribute emitted by the parser for TEST/TEST_F/TEST_P/
/// TYPED_TEST macro blocks — one entry per block, TestId
/// `relpath::Suite.Name`. Disable cue: a `GTEST_SKIP()`/`SKIP()` call in the
/// block body.
/// </summary>
public sealed class GoogleTestAdapter : ITestFrameworkAdapter
{
    internal static readonly HashSet<string> Macros = new(StringComparer.Ordinal)
    {
        "TEST", "TEST_F", "TEST_P", "TYPED_TEST", "TYPED_TEST_P"
    };

    public string FrameworkId => "googletest";

    public bool CanAdapt(CodeModel model) =>
        model.AllMethods.Any(m => EntryAttr(m) is { } attr
            && attr.Args.Count > 0 && attr.Args[0].IsLiteral
            && Macros.Contains(attr.Args[0].Value));

    public IReadOnlyList<TestEntry> ExtractEntries(CodeModel model)
    {
        var entries = new List<TestEntry>();
        foreach (var method in model.AllMethods)
        {
            var attr = GoogleTestAdapter.EntryAttr(method);
            if (attr is null || attr.Args.Count < 3 || !attr.Args[0].IsLiteral) continue;
            if (!Macros.Contains(attr.Args[0].Value)) continue;
            var suite = attr.Args[1].Value;
            var name = attr.Args[2].Value;
            var disabled = method.Invocations.Any(IsDisableCue);
            entries.Add(new TestEntry(
                $"{model.RelativePath}::{suite}.{name}",
                model.RelativePath, method.Line, !disabled, method.Name, Array.Empty<string>()));
        }
        return entries;
    }

    internal static AttributeDecl? EntryAttr(MethodDecl method) =>
        method.Attributes.FirstOrDefault(a => a.Name == "cxx.entry");

    /// <summary>Disable cue: GTEST_SKIP()/SKIP() in the macro-block body (TDS 7.2).</summary>
    internal static bool IsDisableCue(InvocationDecl inv) =>
        inv.Receiver is null && inv.Method is "GTEST_SKIP" or "SKIP";
}

/// <summary>
/// Catch2 v3 adapter (TDS 7.2, framework matrix row C++). Entry cue: the
/// `cxx.entry` attribute for TEST_CASE/TEST_CASE_METHOD/METHOD_AS_TEST_CASE/
/// CATCH_TEST_CASE/TEMPLATE_TEST_CASE blocks — TestId `relpath::Title` where
/// the title is the first string-literal macro argument (argument 0 for the
/// plain forms, argument 1 for the fixture/method forms). Disable cue: a
/// `SKIP()` call in the block body.
/// </summary>
public sealed class Catch2Adapter : ITestFrameworkAdapter
{
    internal static readonly HashSet<string> Macros = new(StringComparer.Ordinal)
    {
        "TEST_CASE", "TEST_CASE_METHOD", "CATCH_TEST_CASE",
        "METHOD_AS_TEST_CASE", "TEMPLATE_TEST_CASE"
    };

    /// <summary>Forms whose macro argument 1 (after the fixture/method name) is the title.</summary>
    private static readonly HashSet<string> s_titleAtArg1 = new(StringComparer.Ordinal)
    {
        "TEST_CASE_METHOD", "METHOD_AS_TEST_CASE", "TEMPLATE_TEST_CASE"
    };

    public string FrameworkId => "catch2";

    public bool CanAdapt(CodeModel model) =>
        model.AllMethods.Any(m => GoogleTestAdapter.EntryAttr(m) is { } attr
            && attr.Args.Count > 0 && attr.Args[0].IsLiteral
            && Macros.Contains(attr.Args[0].Value));

    public IReadOnlyList<TestEntry> ExtractEntries(CodeModel model)
    {
        var entries = new List<TestEntry>();
        foreach (var method in model.AllMethods)
        {
            var attr = GoogleTestAdapter.EntryAttr(method);
            if (attr is null || attr.Args.Count < 2 || !attr.Args[0].IsLiteral) continue;
            var macro = attr.Args[0].Value;
            if (!Macros.Contains(macro)) continue;
            var titleIndex = s_titleAtArg1.Contains(macro) ? 2 : 1;
            if (attr.Args.Count <= titleIndex || !attr.Args[titleIndex].IsLiteral) continue;
            var title = attr.Args[titleIndex].Value;
            var disabled = method.Invocations.Any(GoogleTestAdapter.IsDisableCue);
            entries.Add(new TestEntry(
                $"{model.RelativePath}::{title}",
                model.RelativePath, method.Line, !disabled, method.Name, Array.Empty<string>()));
        }
        return entries;
    }
}

/// <summary>
/// Unity adapter (TDS 7.2, framework matrix row C, secondary framework).
/// Entry cue: free functions named `test_*`. A Unity file is only adapted
/// when its runner names tests through the CTest/Unity `RUN_TEST(name)`
/// macro — the file-level scan collects those invocations (the "CTest runner
/// macro handling" of TDS 5.2 M5): a test function not named by any
/// `RUN_TEST` is not executed by this file's runner and is therefore not an
/// entry. Disable cue: a `TEST_IGNORE()` call in the test body.
/// </summary>
public sealed class UnityAdapter : ITestFrameworkAdapter
{
    public string FrameworkId => "unity";

    public bool CanAdapt(CodeModel model) =>
        model.AllMethods.Any(IsTestFunction)
        && model.AllMethods.Any(m => m.Invocations.Any(IsRunTest));

    public IReadOnlyList<TestEntry> ExtractEntries(CodeModel model)
    {
        var runNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in model.AllMethods)
        {
            foreach (var inv in method.Invocations)
            {
                if (!IsRunTest(inv) || inv.Args.Count == 0) continue;
                runNames.Add(inv.Args[0].Value);
            }
        }

        var entries = new List<TestEntry>();
        foreach (var method in model.AllMethods)
        {
            if (!IsTestFunction(method)) continue;
            if (!runNames.Contains(method.Name)) continue; // not run by this file's runner
            var disabled = method.Invocations.Any(IsDisableCue);
            entries.Add(new TestEntry(
                $"{model.RelativePath}::{method.Name}",
                model.RelativePath, method.Line, !disabled, method.Name, Array.Empty<string>()));
        }
        return entries;
    }

    internal static bool IsTestFunction(MethodDecl method) =>
        method.DeclaringType == "<global>" && method.Name.StartsWith("test_", StringComparison.Ordinal);

    internal static bool IsRunTest(InvocationDecl inv) =>
        inv.Receiver is null && inv.Method is "RUN_TEST" or "RUN_TEST_CASE";

    /// <summary>Disable cue: TEST_IGNORE()/SKIP() in the test body (TDS 7.2).</summary>
    internal static bool IsDisableCue(InvocationDecl inv) =>
        inv.Receiver is null && inv.Method is "TEST_IGNORE" or "SKIP";
}

/// <summary>
/// C/C++ registry over the IR-v1 adapters (TDS 7.2). The parser normalizes
/// GoogleTest, Catch2 and Unity entries into `cxx.entry` attributes and
/// `test_*` functions; the registry adapts them in fixed order:
/// googletest, catch2, unity.
/// </summary>
public sealed class CxxTestFrameworkRegistry
{
    private readonly IReadOnlyList<ITestFrameworkAdapter> _adapters = new ITestFrameworkAdapter[]
    {
        new GoogleTestAdapter(),
        new Catch2Adapter(),
        new UnityAdapter()
    };

    public IReadOnlyList<ITestFrameworkAdapter> All => _adapters;

    public IReadOnlyList<ITestFrameworkAdapter> Detect(CodeModel model) =>
        _adapters.Where(a => a.CanAdapt(model)).ToArray();

    public IReadOnlyList<TestEntry> ExtractEntries(CodeModel model)
    {
        var entries = new List<TestEntry>();
        foreach (var adapter in Detect(model))
            entries.AddRange(adapter.ExtractEntries(model));
        return entries;
    }
}
