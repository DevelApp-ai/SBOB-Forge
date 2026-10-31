namespace SBOBForge.Parsing;

/// <summary>
/// Parser backend contract (TDS 5.2). Backends turn one source file into a
/// <see cref="CodeModel"/> or throw <see cref="SBOBForge.Core.ParseHardError"/>.
/// </summary>
public interface ICodeModelParser
{
    string BackendId { get; }

    /// <summary>Parse one file. Parse failures are hard errors (TDS 5.2): never best-effort skip.</summary>
    CodeModel Parse(string relativePath, string sourceText);
}
