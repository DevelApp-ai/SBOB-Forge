using System;
using System.Collections.Generic;
using System.Linq;
using SBOBForge.Adapters;
using SBOBForge.Parsing;

namespace SBOBForge.Adapters.Java;

/// <summary>
/// TestNG adapter (TDS 7.2): @Test entries; enabled=false (named argument)
/// and @Ignore produce disabled entries.
/// </summary>
public sealed class TestNGAdapter : ITestFrameworkAdapter
{
    public string FrameworkId => "testng";

    public bool CanAdapt(CodeModel model) =>
        model.ImportList.Any(i => i.StartsWith("org.testng", StringComparison.Ordinal));

    public IReadOnlyList<TestEntry> ExtractEntries(CodeModel model)
    {
        var entries = new List<TestEntry>();
        foreach (var method in model.AllMethods)
        {
            var test = method.Attributes.FirstOrDefault(a => a.Name == "Test");
            if (test is null) continue;
            bool enabled = !test.Args.Any(a =>
                a.IsNamed("enabled") && a.IsLiteral && a.Value is "false" or "False");
            var ignored = method.Attributes.Any(a => a.Name == "Ignore");
            entries.Add(new TestEntry(
                JUnit5Adapter.Qualified(model, method), model.RelativePath, method.Line,
                enabled && !ignored, method.Name, Array.Empty<string>()));
        }
        return entries;
    }
}
