using System;
using System.Collections.Generic;
using System.Linq;
using SBOBForge.Adapters;
using SBOBForge.Parsing;

namespace SBOBForge.Adapters.PowerShell;

/// <summary>
/// Pester v5 adapter (TDS 7.2, framework matrix row PowerShell; lexical
/// parser, reduced confidence). The parser synthesizes one "ps.entry"-
/// attributed method per `It` block carrying the title, full describe path,
/// and resolved state (`It -Skip` → skip). Entries from lexical parsers are
/// marked in every downstream artifact (TDS 5.4).
/// </summary>
public sealed class PesterAdapter : ITestFrameworkAdapter
{
    public const string AttributeName = "ps.entry";

    public string FrameworkId => "pester";

    public bool CanAdapt(CodeModel model) =>
        model.AllMethods.Any(m => m.Attributes.Any(a => a.Name == AttributeName));

    public IReadOnlyList<TestEntry> ExtractEntries(CodeModel model)
    {
        var entries = new List<TestEntry>();
        foreach (var method in model.AllMethods)
        {
            var attr = method.Attributes.FirstOrDefault(a => a.Name == AttributeName);
            if (attr is null) continue;
            var full = attr.Args.FirstOrDefault(a => a.IsNamed("full"))?.Value
                ?? attr.Args.FirstOrDefault(a => a.IsNamed("title"))?.Value ?? "";
            var state = attr.Args.FirstOrDefault(a => a.IsNamed("state"))?.Value ?? "run";
            entries.Add(new TestEntry(
                $"{model.RelativePath}::{full}",
                model.RelativePath, method.Line, state != "skip", method.Name, Array.Empty<string>()));
        }
        return entries;
    }
}

/// <summary>PowerShell registry over the IR-v1 adapters (TDS 7.2): pester.</summary>
public sealed class PsTestFrameworkRegistry
{
    private readonly IReadOnlyList<ITestFrameworkAdapter> _adapters = new ITestFrameworkAdapter[]
    {
        new PesterAdapter()
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
