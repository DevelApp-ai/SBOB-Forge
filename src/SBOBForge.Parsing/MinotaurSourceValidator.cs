using System;
using System.Reflection;
using System.Threading.Tasks;
using Minotaur.Parser;
using Minotaur.Plugins;

namespace SBOBForge.Parsing;

/// <summary>
/// Minotaur as the pinned source-validation substrate (TDS 5.2/5.3). The
/// public DevelApp.Minotaur package validates C# source and reports parse
/// errors; its installed version is what the grammar lock pins (IR v1).
/// Structural IR extraction uses the Roslyn backend (see RoslynCSharpParser
/// doc note); when a public Minotaur release supports structure-preserving
/// CognitiveGraph parsing, this substrate is the drop-in deep-parse backend.
/// </summary>
public sealed class MinotaurSourceValidator
{
    public string BackendId => "minotaur-stepparser";

    /// <summary>Installed DevelApp.Minotaur assembly version, normalized to major.minor.patch (verified against the lock in --frozen mode).</summary>
    public static string InstalledVersion() =>
        typeof(StepParserIntegration).Assembly.GetName().Version is { } v ? v.ToString(3) : "0.0.0";

    /// <summary>
    /// Validate C# source through Minotaur's StepParser validation pass.
    /// Returns (isValid, errorCount, firstError) with firstError formatted
    /// as "type: message (line N)". Never throws for invalid source; only
    /// for substrate failures.
    /// </summary>
    public static async Task<(bool IsValid, int ErrorCount, string? FirstError)> ValidateAsync(string sourceText)
    {
        using var integration = StepParserIntegrationFactory.CreateForCSharp(new LanguagePluginManager());
        var result = await integration.ValidateSourceAsync(sourceText);
        var first = result.Errors is { Length: > 0 } errs
            ? $"{errs[0].Type}: {errs[0].Message} (line {errs[0].Line})"
            : null;
        return (result.IsValid, result.Errors?.Length ?? 0, first);
    }
}
