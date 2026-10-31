using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using SBOBForge.Adapters.DotNet;
using SBOBForge.Catalog;
using SBOBForge.Core;
using SBOBForge.Core.Config;
using SBOBForge.Core.Discovery;
using SBOBForge.Core.Emit;
using SBOBForge.Core.Model;
using SBOBForge.Parsing;

namespace SBOBForge.Cli;

public sealed class GenerationOptions
{
    public required string RepoRoot { get; init; }
    public required string ConfigPath { get; init; }
    public required string LockPath { get; init; }
    public string? TargetOsOverride { get; init; }
    public bool Strict { get; init; }
    public bool Frozen { get; init; }
    public bool Check { get; init; }
    public required string GeneratorVersion { get; init; }
}

public sealed class GenerationResult
{
    public required string SbobYaml { get; init; }
    public required string ProvenanceJsonl { get; init; }
    public required string CoverageJson { get; init; }
    public required string SbobFileName { get; init; }
    public required string ProvenanceFileName { get; init; }
    public required string CoverageFileName { get; init; }
    public required BehaviorIr Ir { get; init; }
}

/// <summary>
/// The generate pipeline (TDS 7-8, 13): lock verification → discovery →
/// parse → adapt → lower → tier → attribute → IR → emit → validate → write.
/// Every stage is content-deterministic (TDS 9): same inputs, same bytes.
/// </summary>
public static class GeneratePipeline
{
    public static GenerationResult Run(GenerationOptions options)
    {
        var root = Path.GetFullPath(options.RepoRoot);
        var configPath = Path.GetFullPath(options.ConfigPath, root);
        var lockPath = Path.GetFullPath(options.LockPath, root);

        var config = SbobForgeConfig.Load(configPath);
        var targetOs = options.TargetOsOverride ?? config.TargetOs;
        var package = new PackageIdentity(config.PackageType, config.PackageName, config.PackageVersion, targetOs);
        var configHash = SbobForgeConfig.ComputeHash(configPath);

        var grammarLock = GrammarLock.Load(lockPath); // throws with guidance when missing
        if (options.Frozen)
        {
            grammarLock.VerifyFrozen(MinotaurSourceValidator.InstalledVersion());
            if (grammarLock.Grammars.TryGetValue("csharp", out var pinned))
            {
                var installed = RoslynVersion();
                if (!string.Equals(pinned.Version, installed, StringComparison.Ordinal))
                    throw new LockMismatchException(
                        $"Grammar lock mismatch: sbobforge.lock.json pins Roslyn {pinned.Version}, installed is {installed}. " +
                        "Run 'sbobforge lock --update' and review like a dependency bump.");
            }
        }

        var parser = new RoslynCSharpParser();
        var registry = new TestFrameworkRegistry();

        var sutFiles = FileDiscovery.Enumerate(root, config.SutGlobs);
        var testFiles = FileDiscovery.Enumerate(root, config.TestGlobs);

        var allowedParseError = new HashSet<string>(config.AllowParseError, StringComparer.Ordinal);

        var sutModels = new List<CodeModel>();
        var testModels = new List<(CodeModel Model, IReadOnlyList<TestEntry> Entries, List<TestEntry> Skipped)>();
        var parseErrors = new List<string>();

        foreach (var file in sutFiles)
            sutModels.Add(ParseOrQuarantine(parser, root, file, allowedParseError, parseErrors));

        foreach (var file in testFiles)
        {
            var model = ParseOrQuarantine(parser, root, file, allowedParseError, parseErrors);
            var entries = registry.ExtractEntries(model);
            testModels.Add((model, entries.Where(e => e.Enabled).ToList(), entries.Where(e => !e.Enabled).ToList()));
        }

        // Simple-name index over SUT methods for one-hop test→SUT resolution (M1).
        var sutMethodsByName = sutModels
            .SelectMany(m => m.AllMethods.Select(method => (m, method)))
            .GroupBy(x => x.method.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var execs = new List<ExecEvent>();
        var opens = new List<OpenEvent>();
        var egress = new List<NetEvent>();
        var ingress = new List<NetEvent>();
        var quarantined = new List<QuarantinedItem>();
        var noise = new List<NoiseItem>();
        var unrepresentable = new List<UnrepresentableItem>();

        foreach (var err in parseErrors)
            noise.Add(new NoiseItem("parse error allowed via allowParseError: " + err, err, 0, ""));

        void AddLowered(LoweredInvocation lowered, string testId, string file, int line)
        {
            switch (lowered.Outcome)
            {
                case LiftOutcome.Unrepresentable:
                    unrepresentable.Add(new UnrepresentableItem(lowered.UnrepresentablePrimitive ?? "unknown", file, line, testId));
                    return;
                case LiftOutcome.Quarantine:
                    quarantined.Add(new QuarantinedItem(lowered.Primitive, lowered.Reason ?? "unresolvable", file, line, testId, lowered.CatalogRuleId));
                    return;
                case LiftOutcome.Event:
                    break;
            }

            switch (lowered.Primitive)
            {
                case BehaviorPrimitive.Open:
                {
                    var (path, tier) = ApplyPathPolicies(lowered.Path!, config);
                    var provenance = new List<Provenance> { new(testId, file, line, tier, lowered.CatalogRuleId) };
                    opens.Add(new OpenEvent(path, lowered.Flags, tier, provenance));
                    break;
                }
                case BehaviorPrimitive.Exec:
                {
                    // Bare exec names are never resolved against the host PATH (TDS 9):
                    // they must map through execPathPolicy or stay in quarantine.
                    if (!lowered.Path!.Contains('/') && !config.ExecPathPolicy.ContainsKey(lowered.Path))
                    {
                        quarantined.Add(new QuarantinedItem(
                            BehaviorPrimitive.Exec,
                            $"bare exec name '{lowered.Path}' has no execPathPolicy mapping; never resolved against the host PATH",
                            file, line, testId, lowered.CatalogRuleId));
                        return;
                    }
                    var (path, tier) = ApplyExecPolicy(lowered.Path, config);
                    var provenance = new List<Provenance> { new(testId, file, line, tier, lowered.CatalogRuleId) };
                    var argv = lowered.ArgsAllLiteral
                        ? new[] { path }.Concat(lowered.Args).ToArray()
                        : new[] { path, CanonicalSbobEmitter.ExecArgsWildcard };
                    execs.Add(new ExecEvent(path, argv, true, tier, provenance));
                    break;
                }
                case BehaviorPrimitive.Connect:
                {
                    var provenance = new List<Provenance> { new(testId, file, line, ResolutionTier.Literal, lowered.CatalogRuleId) };
                    egress.Add(new NetEvent(lowered.DnsName ?? "", Array.Empty<string>(), lowered.Port, lowered.Protocol, NetDirection.Egress, ResolutionTier.Literal, provenance));
                    break;
                }
                case BehaviorPrimitive.Listen:
                {
                    var provenance = new List<Provenance> { new(testId, file, line, ResolutionTier.Literal, lowered.CatalogRuleId) };
                    ingress.Add(new NetEvent(lowered.DnsName ?? "", Array.Empty<string>(), lowered.Port, lowered.Protocol, NetDirection.Ingress, ResolutionTier.Literal, provenance));
                    break;
                }
            }
        }

        // 1) SUT-file invocations: direct events, attributed to the SUT method.
        foreach (var model in sutModels)
        {
            foreach (var method in model.AllMethods)
            {
                foreach (var inv in method.Invocations)
                {
                    var lowered = DotNetCatalogLowering.Lower(inv);
                    if (lowered is null) continue;
                    AddLowered(lowered, "sut:" + method.QualifiedName, model.RelativePath, inv.Line);
                }
            }
        }

        // 2) Test-file invocations inside enabled entries: events attributed to the
        // test; one-hop SUT calls pull the SUT method body; everything else is harness noise.
        foreach (var (model, entries, skipped) in testModels)
        {
            foreach (var skippedEntry in skipped)
                noise.Add(new NoiseItem("skipped test excluded (harness behavior not profiled)",
                    skippedEntry.File, skippedEntry.Line, skippedEntry.TestId));

            var entriesByMethod = entries
                .GroupBy(e => e.EntryMethod, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

            foreach (var method in model.AllMethods)
            {
                var hasInvocations = method.Invocations.Count > 0;
                if (!entriesByMethod.TryGetValue(method.Name, out var methodEntries))
                {
                    if (hasInvocations)
                        foreach (var inv in method.Invocations)
                            noise.Add(new NoiseItem($"test helper (not an entry method): {inv.Receiver}.{inv.Method}",
                                model.RelativePath, inv.Line, ""));
                    continue;
                }

                foreach (var entry in methodEntries)
                {
                    // M1: theory-case values bound by parameter name (ordinal-aligned).
                    CallArg? Substitute(CallArg arg)
                    {
                        if (arg.Kind != ArgKind.Identifier) return null;
                        var index = -1;
                        for (var i = 0; i < method.Parameters.Count; i++)
                        {
                            if (string.Equals(method.Parameters[i], arg.Value, StringComparison.Ordinal)) { index = i; break; }
                        }
                        if (index < 0 || index >= entry.TheoryCaseValues.Count) return null;
                        return CallArg.Literal(entry.TheoryCaseValues[index]);
                    }

                    foreach (var inv in method.Invocations)
                    {
                        // One-hop: direct call into a SUT method → pull its body invocations.
                        if (sutMethodsByName.TryGetValue(inv.Method, out var targets))
                        {
                            foreach (var (sutModel, sutMethod) in targets)
                            {
                                foreach (var sutInv in sutMethod.Invocations)
                                {
                                    var sutLowered = DotNetCatalogLowering.Lower(sutInv);
                                    if (sutLowered is null) continue;
                                    AddLowered(sutLowered, entry.TestId, sutModel.RelativePath, sutInv.Line);
                                }
                            }
                            continue;
                        }

                        var lowered = DotNetCatalogLowering.Lower(inv, Substitute);
                        if (lowered is null)
                        {
                            noise.Add(new NoiseItem($"harness API: {inv.Receiver}.{inv.Method}",
                                model.RelativePath, inv.Line, entry.TestId));
                            continue;
                        }
                        AddLowered(lowered, entry.TestId, model.RelativePath, inv.Line);
                    }
                }
            }
        }

        var coverage = new CoverageReport(
            quarantined.OrderBy(q => q.File, StringComparer.Ordinal).ThenBy(q => q.Line).ThenBy(q => q.TestId, StringComparer.Ordinal).ToArray(),
            noise.OrderBy(n => n.File, StringComparer.Ordinal).ThenBy(n => n.Line).ThenBy(n => n.TestId, StringComparer.Ordinal).ToArray(),
            unrepresentable.OrderBy(u => u.File, StringComparer.Ordinal).ThenBy(u => u.Line).ThenBy(u => u.TestId, StringComparer.Ordinal).ToArray(),
            sutFiles.Count + testFiles.Count, testFiles.Count,
            testModels.Sum(t => t.Entries.Count));

        var ir = new BehaviorIr(
            package, execs, opens, egress, ingress, coverage,
            grammarLock.ComputeLockHash(), configHash, config.Architectures);

        var yaml = CanonicalSbobEmitter.Emit(ir, options.GeneratorVersion, config.Namespace);
        var validationErrors = SBOBForge.Core.Validation.SbobValidator.Validate(yaml);
        if (validationErrors.Count > 0)
            throw new ParseHardError("Emitted SBOB failed self-validation:\n  " +
                string.Join("\n  ", validationErrors));

        if (options.Strict && coverage.Quarantined.Count > 0)
            throw new StrictQuarantineException(
                $"--strict: {coverage.Quarantined.Count} quarantined candidate(s) — inspect the coverage report.");

        var meta = package.MetadataName;
        return new GenerationResult
        {
            SbobYaml = yaml,
            ProvenanceJsonl = EmitProvenance(ir),
            CoverageJson = EmitCoverage(ir),
            SbobFileName = $"sbob/{meta}.sbob.yaml",
            ProvenanceFileName = $"sbob/{meta}.provenance.jsonl",
            CoverageFileName = $"sbob/{meta}.coverage.json",
            Ir = ir
        };
    }

    private static CodeModel ParseOrQuarantine(
        RoslynCSharpParser parser, string root, string file, HashSet<string> allowed, List<string> parseErrors)
    {
        var text = File.ReadAllText(Path.Combine(root, file));
        try
        {
            return parser.Parse(file, text);
        }
        catch (ParseHardError)
        {
            if (allowed.Contains(file))
            {
                parseErrors.Add(file);
                return new CodeModel(file, Array.Empty<TypeDecl>());
            }
            throw;
        }
    }

    private static (string Path, ResolutionTier Tier) ApplyPathPolicies(string path, SbobForgeConfig config)
    {
        foreach (var policy in config.PathPolicies)
        {
            if (policy.Prefix && path.StartsWith(policy.Match, StringComparison.Ordinal))
                return (policy.Emit + path[policy.Match.Length..], ResolutionTier.Pattern);
            if (!policy.Prefix && path == policy.Match)
                return (policy.Emit, ResolutionTier.Pattern);
        }
        return (path, ResolutionTier.Literal);
    }

    private static (string Path, ResolutionTier Tier) ApplyExecPolicy(string target, SbobForgeConfig config)
    {
        if (target.Contains('/'))
            return (target, ResolutionTier.Literal);
        // Bare name: must have an execPathPolicy mapping (checked by caller).
        return (config.ExecPathPolicy[target], ResolutionTier.Pattern);
    }

    private static string EmitProvenance(BehaviorIr ir)
    {
        var sb = new StringBuilder();
        void Row(string primitive, string identifier, Provenance p)
        {
            sb.Append(JsonSerializer.Serialize(new ProvenanceRow(
                primitive, identifier, p.TestId, p.File, p.Line, p.Tier.ToString(), p.CatalogRuleId), Json.Options
            ).Replace("\r", "").Replace("\n", ""));
            sb.Append('\n');
        }
        foreach (var e in ir.Execs)
            foreach (var p in e.Provenance) Row("exec", e.Path, p);
        foreach (var o in ir.Opens)
            foreach (var p in o.Provenance) Row("open", o.Path, p);
        foreach (var n in ir.Egress)
            foreach (var p in n.Provenance) Row("egress", n.DnsName, p);
        foreach (var n in ir.Ingress)
            foreach (var p in n.Provenance) Row("ingress", n.DnsName.Length > 0 ? n.DnsName : ":" + n.Port, p);
        return sb.ToString();
    }

    private static string EmitCoverage(BehaviorIr ir) => JsonSerializer.Serialize(new CoverageDocument(
        ir.IrVersion, ir.ComputeBehaviorHash(),
        ir.Coverage.FilesAnalyzed, ir.Coverage.TestFiles, ir.Coverage.TestEntries,
        ir.Coverage.Quarantined.Select(q => new CoverageRow(
            q.Primitive.ToString(), q.Reason, q.File, q.Line, q.TestId, q.CatalogRuleId)).ToArray(),
        ir.Coverage.Noise.Select(n => new NoiseRow(n.Description, n.File, n.Line, n.TestId)).ToArray(),
        ir.Coverage.Unrepresentable.Select(u => new CoverageRow(
            "unrepresentable", u.Primitive, u.File, u.Line, u.TestId, "")).ToArray()), Json.Options) + "\n";

    public static string RoslynVersion() =>
        typeof(Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private sealed record ProvenanceRow(
        string Primitive, string Identifier, string TestId, string File, int Line, string Tier, string Rule);

    private sealed record CoverageRow(
        string Primitive, string Reason, string File, int Line, string TestId, string Rule);

    private sealed record NoiseRow(string Description, string File, int Line, string TestId);

    private sealed record CoverageDocument(
        int SbobIrVersion,
        string BehaviorHash,
        int FilesAnalyzed,
        int TestFiles,
        int TestEntries,
        CoverageRow[] Quarantined,
        NoiseRow[] Noise,
        CoverageRow[] Unrepresentable);
}
