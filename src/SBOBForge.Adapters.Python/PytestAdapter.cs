using System;
using System.Collections.Generic;
using System.Linq;
using SBOBForge.Adapters;
using SBOBForge.Parsing;

namespace SBOBForge.Adapters.Python;

/// <summary>
/// pytest adapter (TDS 7.2): test_* module functions and Test*/test_*
/// class methods; @pytest.mark.skip/skipif/xfail produce disabled entries;
/// @pytest.mark.parametrize("name", [literals]) produces one entry per case
/// (single-parameter fast path; multi-parameter parametrize keeps one entry).
/// unittest.TestCase classes are left to <see cref="UnittestAdapter"/>.
/// </summary>
public sealed class PytestAdapter : ITestFrameworkAdapter
{
    public string FrameworkId => "pytest";

    public bool CanAdapt(CodeModel model) =>
        model.AllMethods.Any(m => m.Attributes.Any(a => a.Name.StartsWith("pytest.", StringComparison.Ordinal)))
        || model.Types.Any(t => t.Methods.Any(m => IsTestName(m.Name)));

    public IReadOnlyList<TestEntry> ExtractEntries(CodeModel model)
    {
        var entries = new List<TestEntry>();
        foreach (var type in model.Types)
        {
            bool isUnittestClass = type.BaseTypeList.Any(b => b.EndsWith("TestCase", StringComparison.Ordinal));
            foreach (var method in type.Methods)
            {
                if (!IsTestName(method.Name)) continue;
                if (isUnittestClass) continue; // unittest owns TestCase methods
                AddEntries(entries, model, method, type);
            }
        }
        return entries;
    }

    internal static void AddEntries(List<TestEntry> entries, CodeModel model, MethodDecl method, TypeDecl type)
    {
        var disabled = method.Attributes.Any(a => a.Name is "pytest.mark.skip" or "pytest.mark.xfail")
            || method.Attributes.Any(a => a.Name == "pytest.mark.skipif"
                && a.Args.Count > 0
                && a.Args[0].Kind == ArgKind.Literal
                && a.Args[0].Value == "True");
        string testId = Qualified(model, type, method);

        var parametrize = method.Attributes.FirstOrDefault(a => a.Name == "pytest.mark.parametrize");
        if (parametrize is not null)
        {
            var nameArg = parametrize.Args.FirstOrDefault(a => a.IsLiteral);
            var valuesArg = parametrize.Args.FirstOrDefault(a => a.Kind == ArgKind.List);
            if (nameArg is not null && valuesArg is not null && !nameArg.Value.Contains(','))
            {
                var caseIndex = 0;
                foreach (var value in valuesArg.ListElements())
                {
                    entries.Add(new TestEntry(
                        testId + $"[{caseIndex}]", model.RelativePath, method.Line, !disabled,
                        method.Name, new[] { value }));
                    caseIndex++;
                }
                if (caseIndex > 0) return;
            }
        }
        entries.Add(new TestEntry(
            testId, model.RelativePath, method.Line, !disabled, method.Name, Array.Empty<string>()));
    }

    internal static bool IsTestName(string name) =>
        name.StartsWith("test_", StringComparison.Ordinal) || name.StartsWith("Test", StringComparison.Ordinal);

    /// <summary>TDS 6 canonical test id: relpath::Type::method (module functions: relpath::function).</summary>
    internal static string Qualified(CodeModel model, TypeDecl type, MethodDecl m) =>
        type.Name == ModuleStemOf(model)
            ? $"{model.RelativePath}::{m.Name}"
            : $"{model.RelativePath}::{type.Name}::{m.Name}";

    private static string ModuleStemOf(CodeModel model)
    {
        string name = model.RelativePath;
        int slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        if (slash >= 0) name = name[(slash + 1)..];
        int dot = name.LastIndexOf('.');
        if (dot > 0) name = name[..dot];
        return name;
    }
}
