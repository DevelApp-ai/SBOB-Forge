using System;
using System.Collections.Generic;
using System.Linq;
using SBOBForge.Adapters;
using SBOBForge.Parsing;

namespace SBOBForge.Adapters.Go;

/// <summary>
/// go test adapter (TDS 7.2, framework matrix row Go): entry cue is the
/// `go test` naming convention — func TestXxx(t *testing.T). Ignored-test
/// cues t.Skip / t.Skipf are body invocations, not attributes, and disable
/// the entry. testify suites use the same TestXxx naming convention, so one
/// adapter owns the language's entry surface.
/// </summary>
public sealed class GoTestAdapter : ITestFrameworkAdapter
{
    public string FrameworkId => "go-testing";

    public bool CanAdapt(CodeModel model) =>
        model.AllMethods.Any(IsTestFunc);

    public IReadOnlyList<TestEntry> ExtractEntries(CodeModel model)
    {
        var entries = new List<TestEntry>();
        foreach (var method in model.AllMethods)
        {
            if (!IsTestFunc(method)) continue;
            var disabled = method.Invocations.Any(inv =>
                inv.Receiver == "t" && inv.Method is "Skip" or "Skipf");
            entries.Add(new TestEntry(
                $"{model.RelativePath}::{method.Name}",
                model.RelativePath, method.Line, !disabled, method.Name, Array.Empty<string>()));
        }
        return entries;
    }

    /// <summary>go test convention: exported func with Test prefix and a *testing.T parameter (by convention named t).</summary>
    internal static bool IsTestFunc(MethodDecl method) =>
        method.Name.StartsWith("Test", StringComparison.Ordinal)
        && method.Parameters.Contains("t");
}
