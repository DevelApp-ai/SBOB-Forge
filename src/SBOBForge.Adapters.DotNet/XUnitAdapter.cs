using System;
using System.Collections.Generic;
using System.Linq;
using SBOBForge.Parsing;

namespace SBOBForge.Adapters.DotNet;

/// <summary>
/// xUnit adapter: [Fact] entries and [Theory] with [InlineData] cases.
/// [Fact(Skip=...)] / [Theory(Skip=...)] produce disabled entries (TDS 7.4:
/// excluded from sections, reported as noise).
/// </summary>
public sealed class XUnitAdapter : ITestFrameworkAdapter
{
    public string FrameworkId => "xunit";

    public bool CanAdapt(CodeModel model) =>
        model.AllMethods.Any(m => m.Attributes.Any(HasMarker));

    public IReadOnlyList<TestEntry> ExtractEntries(CodeModel model)
    {
        var entries = new List<TestEntry>();
        foreach (var method in model.AllMethods)
        {
            var marker = method.Attributes.FirstOrDefault(HasMarker);
            if (marker is null) continue;
            var disabled = marker.Args.Any(a => a.IsNamed("Skip"));

            if (marker.Name == "Theory")
            {
                var caseIndex = 0;
                foreach (var inline in method.Attributes.Where(a => a.Name == "InlineData"))
                {
                    var values = inline.Args.Where(a => a.IsLiteral).Select(a => a.Value).ToList();
                    entries.Add(new TestEntry(
                        Qualified(model, method) + $"[{caseIndex}]",
                        model.RelativePath, method.Line, !disabled, method.Name, values));
                    caseIndex++;
                }
                if (caseIndex == 0)
                    entries.Add(new TestEntry(
                        Qualified(model, method), model.RelativePath, method.Line, !disabled, method.Name, Array.Empty<string>()));
            }
            else
            {
                entries.Add(new TestEntry(
                    Qualified(model, method), model.RelativePath, method.Line, !disabled, method.Name, Array.Empty<string>()));
            }
        }
        return entries;
    }

    private static bool HasMarker(AttributeDecl a) => a.Name is "Fact" or "Theory";

    internal static string Qualified(CodeModel model, MethodDecl m) =>
        (string.IsNullOrEmpty(m.Namespace) ? "" : m.Namespace + ".") + m.DeclaringType + "." + m.Name;
}
