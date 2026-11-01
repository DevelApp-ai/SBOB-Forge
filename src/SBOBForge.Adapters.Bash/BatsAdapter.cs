using System;
using System.Collections.Generic;
using System.Linq;
using SBOBForge.Adapters;
using SBOBForge.Parsing;

namespace SBOBForge.Adapters.Bash;

/// <summary>
/// Bats adapter (TDS 7.2, framework matrix row Bash; lexical parser, reduced
/// confidence). The parser synthesizes one "sh.entry"-attributed method per
/// `@test "title" { … }` block; a `skip` command in the block body disables
/// the entry. Entries from lexical parsers are marked in every downstream
/// artifact (TDS 5.4).
/// </summary>
public sealed class BatsAdapter : ITestFrameworkAdapter
{
    public const string AttributeName = "sh.entry";

    public string FrameworkId => "bats";

    public bool CanAdapt(CodeModel model) =>
        model.AllMethods.Any(m => m.Attributes.Any(a => a.Name == AttributeName));

    public IReadOnlyList<TestEntry> ExtractEntries(CodeModel model)
    {
        var entries = new List<TestEntry>();
        foreach (var method in model.AllMethods)
        {
            var attr = method.Attributes.FirstOrDefault(a => a.Name == AttributeName);
            if (attr is null) continue;
            var title = attr.Args.FirstOrDefault(a => a.IsNamed("title"))?.Value ?? "";
            var state = attr.Args.FirstOrDefault(a => a.IsNamed("state"))?.Value ?? "run";
            entries.Add(new TestEntry(
                $"{model.RelativePath}::{title}",
                model.RelativePath, method.Line, state != "skip", method.Name, Array.Empty<string>()));
        }
        return entries;
    }
}

/// <summary>Bash registry over the IR-v1 adapters (TDS 7.2): bats.</summary>
public sealed class ShTestFrameworkRegistry
{
    private readonly IReadOnlyList<ITestFrameworkAdapter> _adapters = new ITestFrameworkAdapter[]
    {
        new BatsAdapter()
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
