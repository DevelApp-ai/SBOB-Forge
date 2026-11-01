using System;
using System.Collections.Generic;
using System.Linq;
using SBOBForge.Adapters;
using SBOBForge.Parsing;

namespace SBOBForge.Adapters.Php;

/// <summary>
/// PHPUnit adapter (TDS 7.2, framework matrix row PHP). Entry cues: the
/// `test` method-name prefix, the `#[Test]` attribute (PHPUnit 10/11), and
/// the docblock `@test` marker. Disable cues: `$this->markTestSkipped()` /
/// `$this->markTestIncomplete()` body calls (receiver "this") and the
/// docblock `@requires` marker. One adapter owns the language's entry surface
/// (the runtime is the framework).
/// </summary>
public sealed class PhpUnitAdapter : ITestFrameworkAdapter
{
    public string FrameworkId => "phpunit";

    public bool CanAdapt(CodeModel model) =>
        model.AllMethods.Any(IsTestMethod) || model.Types.Any(t => t.BaseTypeList.Any(IsTestCase));

    public IReadOnlyList<TestEntry> ExtractEntries(CodeModel model)
    {
        var entries = new List<TestEntry>();
        foreach (var type in model.Types)
        {
            foreach (var method in type.Methods)
            {
                if (!IsTestMethod(method)) continue;
                var disabled = IsDisabled(method);
                entries.Add(new TestEntry(
                    $"{model.RelativePath}::{type.Name}::{method.Name}",
                    model.RelativePath, method.Line, !disabled, method.Name, Array.Empty<string>()));
            }
        }
        return entries;
    }

    /// <summary>Entry cue: `test` prefix, `#[Test]` attribute, or docblock `@test` (TDS 7.2).</summary>
    internal static bool IsTestMethod(MethodDecl method) =>
        method.Name.StartsWith("test", StringComparison.Ordinal)
        || method.Attributes.Any(a => a.Name is "Test" or "@test");

    /// <summary>Disable cue: markTestSkipped/markTestIncomplete call or docblock `@requires` (TDS 7.2).</summary>
    internal static bool IsDisabled(MethodDecl method) =>
        method.Attributes.Any(a => a.Name == "@requires")
        || method.Invocations.Any(inv =>
            inv.Receiver == "this" && inv.Method is "markTestSkipped" or "markTestIncomplete");

    /// <summary>`extends TestCase` / `extends \PHPUnit\Framework\TestCase` (last segment "TestCase").</summary>
    private static bool IsTestCase(string baseType) =>
        baseType == "TestCase"
        || (baseType.Contains('\\') && baseType.Split('\\')[^1] == "TestCase");
}

/// <summary>
/// PHP registry over the IR-v1 adapters (TDS 7.2). PHPUnit owns the entry
/// surface; the registry also adapts files whose classes merely extend
/// TestCase, so the entry cues come from methods or the base type in fixed
/// order: phpunit.
/// </summary>
public sealed class PhpTestFrameworkRegistry
{
    private readonly IReadOnlyList<ITestFrameworkAdapter> _adapters = new ITestFrameworkAdapter[]
    {
        new PhpUnitAdapter()
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
