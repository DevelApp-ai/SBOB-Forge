namespace SBOBForge.Core;

/// <summary>Exit code 2 (TDS 13): parse/validation hard error. A file that cannot be parsed could hide behavior.</summary>
public sealed class ParseHardError(string message) : Exception(message);

/// <summary>Exit code 4 (TDS 13): quarantine non-empty under --strict.</summary>
public sealed class StrictQuarantineException(string message) : Exception(message);

/// <summary>Exit code 3 (TDS 13): byte drift detected by --check (org decision #26).</summary>
public sealed class DriftDetectedException(string message) : Exception(message);
