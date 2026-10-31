using System;
using System.Collections.Generic;
using System.Linq;
using SBOBForge.Parsing;

namespace SBOBForge.Adapters.DotNet;

/// <summary>
/// MSTest adapter: [TestMethod] entries; [Ignore]/[TestMethod(Ignore=...)] disabling.
/// </summary>
public sealed class MSTestAdapter : ITestFrameworkAdapter
{
    public string FrameworkId => "mstest";

    public bool CanAdapt(CodeModel model) =>
        model.AllMethods.Any(m => m.Attributes.Any(a => a.Name is "TestMethod" or "TestClass" or "DataTestMethod"));

    public IReadOnlyList<TestEntry> ExtractEntries(CodeModel model)
    {
        var entries = new List<TestEntry>();
        foreach (var method in model.AllMethods)
        {
            var marker = method.Attributes.FirstOrDefault(a => a.Name is "TestMethod" or "DataTestMethod");
            if (marker is null) continue;
            var disabled = method.Attributes.Any(a => a.Name == "Ignore")
                || marker.Args.Any(a => a.IsNamed("Ignore"));

            // DataRow cases on [DataTestMethod] behave like xUnit InlineData.
            var cases = method.Attributes.Where(a => a.Name == "DataRow").ToList();
            if (marker.Name == "DataTestMethod" && cases.Count > 0)
            {
                var caseIndex = 0;
                foreach (var c in cases)
                {
                    var values = c.Args.Where(a => a.IsLiteral).Select(a => a.Value).ToList();
                    entries.Add(new TestEntry(
                        XUnitAdapter.Qualified(model, method) + $"[{caseIndex}]",
                        model.RelativePath, method.Line, !disabled, method.Name, values));
                    caseIndex++;
                }
            }
            else
            {
                entries.Add(new TestEntry(
                    XUnitAdapter.Qualified(model, method), model.RelativePath, method.Line, !disabled, method.Name, Array.Empty<string>()));
            }
        }
        return entries;
    }
}
