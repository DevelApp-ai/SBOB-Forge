using System;
using System.Collections.Generic;
using System.Linq;
using SBOBForge.Adapters;
using SBOBForge.Parsing;

namespace SBOBForge.Adapters.Python;

/// <summary>
/// unittest adapter (TDS 7.2): test_* methods on unittest.TestCase
/// subclasses; @unittest.skip/skipIf/skipUnless produce disabled entries.
/// </summary>
public sealed class UnittestAdapter : ITestFrameworkAdapter
{
    public string FrameworkId => "unittest";

    public bool CanAdapt(CodeModel model) =>
        model.Types.Any(t => t.BaseTypeList.Any(b => b.EndsWith("TestCase", StringComparison.Ordinal)));

    public IReadOnlyList<TestEntry> ExtractEntries(CodeModel model)
    {
        var entries = new List<TestEntry>();
        foreach (var type in model.Types)
        {
            bool isTestCase = type.BaseTypeList.Any(b => b.EndsWith("TestCase", StringComparison.Ordinal));
            if (!isTestCase) continue;
            foreach (var method in type.Methods)
            {
                if (!PytestAdapter.IsTestName(method.Name)) continue;
                var disabled = IsDisabled(method);
                entries.Add(new TestEntry(
                    $"{model.RelativePath}::{type.Name}::{method.Name}",
                    model.RelativePath, method.Line, !disabled, method.Name, Array.Empty<string>()));
            }
        }
        return entries;
    }

    /// <summary>
    /// Statically evaluated skip markers: <c>skip</c> always disables;
    /// <c>skipIf</c>/<c>skipUnless</c> disable only when their literal
    /// boolean condition resolves (skipIf(False)/skipUnless(True) run and
    /// stay enabled; non-literal conditions cannot prove a skip → enabled).
    /// </summary>
    internal static bool IsDisabled(MethodDecl method)
    {
        foreach (var attr in method.Attributes)
        {
            switch (attr.Name)
            {
                case "unittest.skip":
                    return true;
                case "unittest.skipIf":
                    return attr.Args.Count > 0
                        && attr.Args[0].Kind == ArgKind.Literal && attr.Args[0].Value == "True";
                case "unittest.skipUnless":
                    return attr.Args.Count > 0
                        && attr.Args[0].Kind == ArgKind.Literal
                        && attr.Args[0].Value != "True" && attr.Args[0].Value != "None";
            }
        }
        return false;
    }
}
