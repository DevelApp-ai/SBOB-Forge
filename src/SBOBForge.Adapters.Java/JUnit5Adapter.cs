using System;
using System.Collections.Generic;
using System.Linq;
using SBOBForge.Adapters;
using SBOBForge.Parsing;

namespace SBOBForge.Adapters.Java;

/// <summary>
/// JUnit 5 adapter (TDS 7.2): @Test entries and @ParameterizedTest with
/// @ValueSource / @CsvSource cases. @Disabled (method-level) produces a
/// disabled entry. Test ids are "pkg.Type.method" with case-ordinal suffixes.
/// </summary>
public sealed class JUnit5Adapter : ITestFrameworkAdapter
{
    public string FrameworkId => "junit5";

    public bool CanAdapt(CodeModel model) =>
        model.ImportList.Any(i => i.StartsWith("org.junit.jupiter", StringComparison.Ordinal))
        || model.AllMethods.Any(m => m.Attributes.Any(a =>
            a.Name is "ParameterizedTest" or "ValueSource" or "CsvSource" or "RepeatedTest"));

    public IReadOnlyList<TestEntry> ExtractEntries(CodeModel model)
    {
        var entries = new List<TestEntry>();
        foreach (var method in model.AllMethods)
        {
            var hasTest = method.Attributes.Any(a => a.Name == "Test");
            var parameterized = method.Attributes.FirstOrDefault(a => a.Name == "ParameterizedTest");
            if (!hasTest && parameterized is null) continue;
            var disabled = method.Attributes.Any(a => a.Name == "Disabled");

            if (parameterized is not null)
            {
                var cases = CollectCases(method);
                var caseIndex = 0;
                foreach (var values in cases)
                {
                    entries.Add(new TestEntry(
                        Qualified(model, method) + $"[{caseIndex}]",
                        model.RelativePath, method.Line, !disabled, method.Name, values));
                    caseIndex++;
                }
                if (caseIndex == 0)
                    entries.Add(new TestEntry(
                        Qualified(model, method), model.RelativePath, method.Line, !disabled, method.Name,
                        Array.Empty<string>()));
            }
            else
            {
                entries.Add(new TestEntry(
                    Qualified(model, method), model.RelativePath, method.Line, !disabled, method.Name,
                    Array.Empty<string>()));
            }
        }
        return entries;
    }

    /// <summary>Case values per case: @ValueSource(strings={...}) / ints/longs; @CsvSource lines split on commas.</summary>
    private static List<string[]> CollectCases(MethodDecl method)
    {
        var cases = new List<string[]>();
        foreach (var attr in method.Attributes)
        {
            if (attr.Name == "ValueSource")
            {
                foreach (var arg in attr.Args)
                {
                    foreach (var value in arg.ListElements())
                        cases.Add(new[] { value });
                }
            }
            else if (attr.Name == "CsvSource")
            {
                foreach (var arg in attr.Args)
                {
                    foreach (var line in arg.ListElements())
                        cases.Add(line.Split(',').Select(p => p.Trim()).ToArray());
                }
            }
        }
        return cases;
    }

    internal static string Qualified(CodeModel model, MethodDecl m) =>
        (string.IsNullOrEmpty(m.Namespace) ? "" : m.Namespace + ".") + m.DeclaringType + "." + m.Name;
}
