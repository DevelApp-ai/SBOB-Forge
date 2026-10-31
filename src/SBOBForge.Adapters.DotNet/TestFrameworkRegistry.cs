using System.Collections.Generic;
using System.Linq;
using SBOBForge.Parsing;

namespace SBOBForge.Adapters.DotNet;

/// <summary>
/// Registry over the IR-v1 framework adapters. Detection is per-file and
/// purely attribute-based; multiple frameworks may adapt the same suite and
/// entries are merged (TDS 7.2). Order is fixed: xunit, nunit, mstest.
/// </summary>
public sealed class TestFrameworkRegistry
{
    private readonly IReadOnlyList<ITestFrameworkAdapter> _adapters = new ITestFrameworkAdapter[]
    {
        new XUnitAdapter(), new NUnitAdapter(), new MSTestAdapter()
    };

    public IReadOnlyList<ITestFrameworkAdapter> All => _adapters;

    /// <summary>Adapters whose marker attributes appear in this model, in fixed registry order.</summary>
    public IReadOnlyList<ITestFrameworkAdapter> Detect(CodeModel model) =>
        _adapters.Where(a => a.CanAdapt(model)).ToArray();

    /// <summary>All entries from all detecting adapters, adapter order then entry order (deterministic).</summary>
    public IReadOnlyList<TestEntry> ExtractEntries(CodeModel model)
    {
        var entries = new List<TestEntry>();
        foreach (var adapter in Detect(model))
            entries.AddRange(adapter.ExtractEntries(model));
        return entries;
    }
}
