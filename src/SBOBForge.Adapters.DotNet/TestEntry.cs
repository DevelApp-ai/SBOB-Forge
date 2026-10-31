namespace SBOBForge.Adapters.DotNet;

/// <summary>
/// One discovered test entry (TDS 7.1-7.2): the unit the pipeline attributes
/// behavior to. Theories produce one entry per InlineData/TestCase case, with
/// case-ordinal suffixes in <see cref="TestId"/> and the case's literal values
/// in <see cref="TheoryCaseValues"/> (aligned with the method's parameters by
/// ordinal, enabling parameter-name propagation in M1).
/// </summary>
public sealed record TestEntry(
    string TestId,
    string File,
    int Line,
    bool Enabled,
    string EntryMethod,
    IReadOnlyList<string> TheoryCaseValues);
