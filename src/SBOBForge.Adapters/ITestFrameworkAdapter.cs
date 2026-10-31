using SBOBForge.Parsing;

namespace SBOBForge.Adapters;

/// <summary>
/// Test-framework adapter contract (TDS 7). Adapters map framework-specific
/// markers (attributes, decorators, naming conventions) to neutral
/// <see cref="TestEntry"/> records. Detection is marker-presence only — no
/// framework assemblies are loaded, so the generator stays deterministic and
/// dependency-light.
/// </summary>
public interface ITestFrameworkAdapter
{
    /// <summary>Framework identifier, e.g. "xunit", "junit5", "pytest".</summary>
    string FrameworkId { get; }

    /// <summary>True when the model contains this framework's markers.</summary>
    bool CanAdapt(CodeModel model);

    /// <summary>Extract entries in deterministic order: type order, then method line order, then case order.</summary>
    IReadOnlyList<TestEntry> ExtractEntries(CodeModel model);
}
