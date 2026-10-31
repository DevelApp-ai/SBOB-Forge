using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SBOBForge.Core.Discovery;

/// <summary>
/// Deterministic file discovery (TDS 5.1, 9): glob expansion with strictly
/// ordinal ordering and normalized ('/') repo-relative paths. No unordered
/// enumeration APIs are used; output is byte-stable across filesystems.
/// </summary>
public static class FileDiscovery
{
    /// <summary>Expand globs (e.g. "src/**\/*.cs", "tests/*.cs") and return a deduplicated, ordinal-sorted list.</summary>
    public static IReadOnlyList<string> Enumerate(string root, IEnumerable<string> globs)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var rawGlob in globs)
        {
            if (string.IsNullOrWhiteSpace(rawGlob)) continue;
            var glob = rawGlob.Trim().Replace('\\', '/');
            foreach (var match in Expand(root, glob))
                set.Add(match);
        }
        return set.ToArray();
    }

    private static IEnumerable<string> Expand(string root, string glob)
    {
        // Split into fixed directory prefix and pattern remainder so common subtrees walk narrowly.
        var segments = glob.Split('/');
        int fixedCount = 0;
        while (fixedCount < segments.Length && !ContainsWildcard(segments[fixedCount])) fixedCount++;

        var fixedRel = string.Join('/', segments.Take(fixedCount));
        var pattern = string.Join('/', segments.Skip(fixedCount));
        var fixedDir = Path.Combine(root, fixedRel);
        if (pattern.Length == 0)
        {
            // Fully fixed path: a single file (or nothing).
            if (File.Exists(fixedDir)) yield return fixedRel;
            yield break;
        }
        if (!Directory.Exists(fixedDir)) yield break;

        // Enumerate everything under the fixed prefix, then match the pattern
        // segment-wise ('**' cannot be pushed into the filesystem enumerator).
        // The pattern is relative to the fixed prefix; yielded paths stay repo-relative.
        var rootLen = root.Length + (root.EndsWith(Path.DirectorySeparatorChar) ? 0 : 1);
        foreach (var full in Directory.EnumerateFiles(fixedDir, "*", SearchOption.AllDirectories))
        {
            var relFromFixed = full[(fixedDir.Length + 1)..].Replace(Path.DirectorySeparatorChar, '/');
            if (!MatchSegments(relFromFixed, pattern)) continue;
            yield return full[rootLen..].Replace(Path.DirectorySeparatorChar, '/');
        }
    }

    private static bool ContainsWildcard(string segment) =>
        segment.Contains('*') || segment.Contains('?') || segment.Contains("⋯");

    /// <summary>Filesystem-level glob for the non-special segments; '…'/'**' handled by MatchSegments.</summary>
    private static string PatternFromGlob(string pattern) =>
        pattern == "**" ? "*" : pattern;

    /// <summary>Segment-wise glob match supporting '**' (any depth) and '*'/'?' within a segment.</summary>
    private static bool MatchSegments(string path, string pattern)
    {
        var pathSegs = path.Split('/');
        var patSegs = pattern.Split('/');

        bool Match(int p, int s)
        {
            while (true)
            {
                if (p == patSegs.Length) return s == pathSegs.Length;
                if (patSegs[p] == "**")
                {
                    if (p + 1 == patSegs.Length) return true;
                    for (var skip = s; skip <= pathSegs.Length; skip++)
                        if (Match(p + 1, skip)) return true;
                    return false;
                }
                if (s == pathSegs.Length) return false;
                if (!MatchSegment(pathSegs[s], patSegs[p])) return false;
                p++; s++;
            }
        }
        return Match(0, 0);
    }

    private static bool MatchSegment(string segment, string pattern)
    {
        // Classic wildcard match with backtracking; ordinal comparisons only.
        int s = 0, p = 0, star = -1, ss = 0;
        while (s < segment.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == segment[s])) { s++; p++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; ss = s; }
            else if (star != -1) { p = star + 1; s = ++ss; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
