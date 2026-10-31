using System;
using System.Collections.Generic;
using System.Linq;
using SBOBForge.Parsing;

using SBOBForge.Adapters;

namespace SBOBForge.Adapters.DotNet;

/// <summary>
/// NUnit adapter: [Test] entries, [TestCase(...)] cases, [Ignore]/[Test(Ignore=...)] disabling.
/// </summary>
public sealed class NUnitAdapter : ITestFrameworkAdapter
{
    public string FrameworkId => "nunit";

    public bool CanAdapt(CodeModel model) =>
        model.AllMethods.Any(m => m.Attributes.Any(a => a.Name is "Test" or "TestCase" or "TestFixture"));

    public IReadOnlyList<TestEntry> ExtractEntries(CodeModel model)
    {
        var entries = new List<TestEntry>();
        foreach (var method in model.AllMethods)
        {
            var marker = method.Attributes.FirstOrDefault(a => a.Name == "Test");
            var cases = method.Attributes.Where(a => a.Name == "TestCase").ToList();
            if (marker is null && cases.Count == 0) continue;
            var disabled = method.Attributes.Any(a => a.Name == "Ignore")
                || marker?.Args.Any(a => a.IsNamed("Ignore")) == true;

            if (cases.Count > 0)
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
