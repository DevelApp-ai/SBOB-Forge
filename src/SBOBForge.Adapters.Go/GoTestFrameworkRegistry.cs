using System.Collections.Generic;
using System.Linq;
using SBOBForge.Adapters;
using SBOBForge.Parsing;

namespace SBOBForge.Adapters.Go;

/// <summary>
/// Go registry over the IR-v1 adapters (TDS 7.2). The stdlib `testing`
/// adapter owns the entry surface; testify adds no entry cues (same
/// TestXxx convention), so entries merge in fixed order: go-testing.
/// </summary>
public sealed class GoTestFrameworkRegistry
{
    private readonly IReadOnlyList<ITestFrameworkAdapter> _adapters = new ITestFrameworkAdapter[]
    {
        new GoTestAdapter()
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
