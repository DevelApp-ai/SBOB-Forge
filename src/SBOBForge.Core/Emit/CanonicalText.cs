namespace SBOBForge.Core.Emit;

/// <summary>
/// Canonical text normalization (TDS 9: platform byte-identity).
///
/// The string-based System.Text.Json serializer uses the platform line ending
/// (<c>Environment.NewLine</c>) as the indented line separator — "\r\n" on
/// Windows, "\n" on Unix (.NET only gains a configurable
/// <c>JsonSerializerOptions.NewLine</c> in .NET 11+). Committed artifacts are
/// LF everywhere and the drift gate compares bytes (org decision #26), so
/// every emitter writing serializer output must normalize to '\n' first.
/// The CLI's provenance emitter already did this per row
/// (".Replace("\r", "").Replace("\n", "")); this makes the normalization a
/// named, tested operation shared by all emitters.
///
/// Removing raw CR cannot corrupt content: the JSON encoder escapes control
/// characters inside serialized string values as backslash sequences, so a
/// raw '\r' byte in serializer output can only ever be a line separator.
/// </summary>
public static class CanonicalText
{
    /// <summary>
    /// Strip CR line separators (CRLF → LF). Idempotent for LF-only text:
    /// emission on Unix is unchanged, so this never regenerates (and never
    /// drifts) any committed golden file.
    /// </summary>
    public static string LfOnly(string text) => text.Replace("\r", "");
}
