using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using SBOBForge.Adapters;
using SBOBForge.Adapters.Bash;
using SBOBForge.Adapters.Cxx;
using SBOBForge.Adapters.DotNet;
using SBOBForge.Adapters.Go;
using SBOBForge.Adapters.Java;
using SBOBForge.Adapters.Php;
using SBOBForge.Adapters.Python;
using SBOBForge.Adapters.PowerShell;
using SBOBForge.Adapters.TypeScript;
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
/// M2: per-language dispatch (.cs → Roslyn/.NET, .java → java17 parser,
/// .py → python311 parser).
/// M3: .go → go119 parser, .js/.ts → typescript parser (Jest/Vitest adapters,
/// js catalog).
/// M4: .php → structural php parser (PHPUnit adapter, php catalog);
/// .ps1/.psm1/.psd1 → structural powershell parser (Pester adapter,
/// powershell catalog); .sh/.bash/.bats → structural bash parser (Bats
/// adapter, bash catalog). TDS 5.4 Phase B upgraded both backends from
/// M4's fixed lexical scanners to structural statement scanners; the
/// reduced-confidence `parser: lexical` markers were retired.
/// M5: .c/.h → structural C17 parser, .cc/.cpp/.cxx/.hpp/.hh/.hxx →
/// structural C++20 parser (GoogleTest/Catch2/Unity adapters, c/cpp
/// catalogs). Socket-setup literals (sin_port/sin_addr) are hoisted onto
/// connect/listen calls as synthetic named args (documented M5 decision).
/// </summary>
public static class GeneratePipeline
{
    internal enum SourceLang { CSharp, Java, Python, Go, TypeScript, Php, PowerShell, Bash, C, Cpp }

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
            grammarLock.VerifyFrozenEntry("csharp", RoslynVersion(), null);
            grammarLock.VerifyFrozenEntry("java17", JavaSourceParser.Version, null);
            grammarLock.VerifyFrozenEntry("python311", PythonSourceParser.Version, null);
            grammarLock.VerifyFrozenEntry("go119", GoSourceParser.Version, null);
            grammarLock.VerifyFrozenEntry("typescript", TypeScriptSourceParser.Version, null);
            grammarLock.VerifyFrozenEntry("php", PhpSourceParser.Version, null);
            grammarLock.VerifyFrozenEntry("powershell", PowerShellSourceParser.Version, PowerShellSourceParser.GrammarSha256);
            grammarLock.VerifyFrozenEntry("bash", BashSourceParser.Version, BashSourceParser.GrammarSha256);
            grammarLock.VerifyFrozenEntry("c17", C17SourceParser.Version, null);
            grammarLock.VerifyFrozenEntry("cpp20", Cpp20SourceParser.Version, null);
            grammarLock.VerifyFrozenEntry(DotNetCatalog.LockEntryName,
                DotNetCatalogLowering.Catalog.Version.ToString(), DotNetCatalog.ComputeDefaultHash());
            grammarLock.VerifyFrozenEntry(JavaCatalog.LockEntryName,
                JavaCatalogLowering.Catalog.Version.ToString(), JavaCatalog.ComputeDefaultHash());
            grammarLock.VerifyFrozenEntry(PythonCatalog.LockEntryName,
                PythonCatalogLowering.Catalog.Version.ToString(), PythonCatalog.ComputeDefaultHash());
            grammarLock.VerifyFrozenEntry(GoCatalog.LockEntryName,
                GoCatalogLowering.Catalog.Version.ToString(), GoCatalog.ComputeDefaultHash());
            grammarLock.VerifyFrozenEntry(JsCatalog.LockEntryName,
                JsCatalogLowering.Catalog.Version.ToString(), JsCatalog.ComputeDefaultHash());
            grammarLock.VerifyFrozenEntry(PhpCatalog.LockEntryName,
                PhpCatalogLowering.Catalog.Version.ToString(), PhpCatalog.ComputeDefaultHash());
            grammarLock.VerifyFrozenEntry(PowerShellCatalog.LockEntryName,
                PowerShellCatalogLowering.Catalog.Version.ToString(), PowerShellCatalog.ComputeDefaultHash());
            grammarLock.VerifyFrozenEntry(BashCatalog.LockEntryName,
                BashCatalogLowering.Catalog.Version.ToString(), BashCatalog.ComputeDefaultHash());
            grammarLock.VerifyFrozenEntry(CCatalog.LockEntryName,
                CCatalogLowering.Catalog.Version.ToString(), CCatalog.ComputeDefaultHash());
            grammarLock.VerifyFrozenEntry(CppCatalog.LockEntryName,
                CppCatalogLowering.Catalog.Version.ToString(), CppCatalog.ComputeDefaultHash());
        }

        var sutFiles = FileDiscovery.Enumerate(root, config.SutGlobs);
        var testFiles = FileDiscovery.Enumerate(root, config.TestGlobs);

        var allowedParseError = new HashSet<string>(config.AllowParseError, StringComparer.Ordinal);

        var sutModels = new List<(CodeModel Model, SourceLang Lang)>();
        var testModels = new List<(CodeModel Model, IReadOnlyList<TestEntry> Entries, List<TestEntry> Skipped)>();
        var parseErrors = new List<string>();

        foreach (var file in sutFiles)
        {
            var lang = LangOf(file);
            sutModels.Add((ParseOrQuarantine(ParserFor(lang), root, file, allowedParseError, parseErrors), lang));
        }

        foreach (var file in testFiles)
        {
            var lang = LangOf(file);
            var model = ParseOrQuarantine(ParserFor(lang), root, file, allowedParseError, parseErrors);
            var entries = RegistryFor(lang)(model);
            testModels.Add((model, entries.Where(e => e.Enabled).ToList(), entries.Where(e => !e.Enabled).ToList()));
        }

        // Simple-name index over SUT methods for one-hop test→SUT resolution (M1),
        // language-scoped so mixed-language repos never cross-match.
        var sutMethodsByName = sutModels
            .SelectMany(x => x.Model.AllMethods.Select(method => (x.Model, x.Lang, method)))
            .GroupBy(x => (x.Lang, x.method.Name))
            .ToDictionary(g => g.Key, g => g.ToList());

        var execs = new List<ExecEvent>();
        var opens = new List<OpenEvent>();
        var egress = new List<NetEvent>();
        var ingress = new List<NetEvent>();
        var quarantined = new List<QuarantinedItem>();
        var noise = new List<NoiseItem>();
        var unrepresentable = new List<UnrepresentableItem>();
        var retired = new List<RetiredItem>();
        var mockShadowed = new List<MockShadowItem>();

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
                {
                    // Declared dynamic-destination placeholder policies (SBOB-Forge #49, TDS 7.3 tier P):
                    // a quarantine whose destination arg is a plain identifier lifts through a
                    // matching policy — variable identity only, never declaration alone, so an
                    // undeclared dynamic destination still fail-closes into the coverage report.
                    if (lowered.ArgIdentifier is { Length: > 0 } identifier)
                    {
                        if (lowered.Primitive == BehaviorPrimitive.Connect
                            && config.NetPolicies.FirstOrDefault(p => string.Equals(p.Match, identifier, StringComparison.Ordinal)) is { } netPolicy)
                        {
                            var provenance = new List<Provenance> { new(testId, file, line, ResolutionTier.Pattern, netPolicy.Id) };
                            // Spec 5.8 (per sbobctl compileDnsPattern, the downstream contract —
                            // TDS design principle 6) admits '⋯' in DNS patterns only BETWEEN two
                            // labels ("a.⋯.b."), so a bare '⋯'/'*' declaration cannot land in
                            // dnsNames. The bare form is the spec 5.7 any-peer entry instead:
                            // ipAddresses: ["*"], no ports — the honest shape of "request-derived
                            // destination, any peer", and it round-trips sbobctl validate.
                            if (netPolicy.Emit == "⋯" || netPolicy.Emit == "*")
                                egress.Add(new NetEvent("", new[] { "*" }, null, "TCP", NetDirection.Egress, ResolutionTier.Pattern, provenance));
                            else
                                egress.Add(new NetEvent(netPolicy.Emit, Array.Empty<string>(), null, "TCP", NetDirection.Egress, ResolutionTier.Pattern, provenance));
                            return;
                        }
                        if (lowered.Primitive == BehaviorPrimitive.Open
                            && config.PathPolicies.FirstOrDefault(p => string.Equals(p.Match, identifier, StringComparison.Ordinal)) is { } pathPolicy)
                        {
                            if (string.IsNullOrWhiteSpace(pathPolicy.Id))
                                throw new InvalidDataException(
                                    $"pathPolicies entry '{{ \"match\": \"{pathPolicy.Match}\" ... }}' lifts identifier-valued open paths " +
                                    $"and must declare an id — it is the coverage-row rule (SBOB-Forge #49): {options.ConfigPath}");
                            var provenance = new List<Provenance> { new(testId, file, line, ResolutionTier.Pattern, pathPolicy.Id) };
                            opens.Add(new OpenEvent(pathPolicy.Emit, lowered.Flags, ResolutionTier.Pattern, provenance));
                            return;
                        }
                    }
                    quarantined.Add(new QuarantinedItem(lowered.Primitive, lowered.Reason ?? "unresolvable", file, line, testId, lowered.CatalogRuleId));
                    return;
                }
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
        foreach (var (model, lang) in sutModels)
        {
            foreach (var method in model.AllMethods)
            {
                foreach (var inv in method.Invocations)
                {
                    var lowered = LowerFor(lang, inv, null);
                    if (lowered is null) continue;
                    AddLowered(lowered, "sut:" + method.QualifiedName, model.RelativePath, inv.Line);
                }
            }
        }

        // 2) Test-file invocations inside enabled entries: events attributed to the
        // test; one-hop SUT calls pull the SUT method body; everything else is harness noise.
        foreach (var (model, entries, skipped) in testModels)
        {
            var lang = LangOf(model.RelativePath);

            foreach (var skippedEntry in skipped)
                retired.Add(new RetiredItem(skippedEntry.TestId, skippedEntry.File, skippedEntry.Line));

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
                    // M1: theory/parametrize case values bound by parameter name (ordinal-aligned).
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
                        // One-hop: direct call into a same-language SUT method → pull its body invocations.
                        if (sutMethodsByName.TryGetValue((lang, inv.Method), out var targets))
                        {
                            foreach (var (sutModel, _, sutMethod) in targets)
                            {
                                foreach (var sutInv in sutMethod.Invocations)
                                {
                                    var sutLowered = LowerFor(lang, sutInv, null);
                                    if (sutLowered is null) continue;
                                    AddLowered(sutLowered, entry.TestId, sutModel.RelativePath, sutInv.Line);
                                }
                            }
                            continue;
                        }

                        var lowered = LowerFor(lang, inv, Substitute);
                        if (lowered is null)
                        {
                            // M7 coverage report v1 (TDS 7.4): mock-framework APIs inside enabled
                            // entries never lower, and where such a mock intercepts a SUT
                            // dependency the SUT's behavior is under-declared — a leading
                            // downstream false-positive source. Surface these as mock-shadowed
                            // warnings instead of absorbing them as generic harness noise.
                            if (MockShadowClassifier.Kind(lang, inv.Receiver, inv.Method) is { } mockKind)
                            {
                                mockShadowed.Add(new MockShadowItem(mockKind,
                                    (inv.Receiver is { Length: > 0 } r ? r + "." : "") + inv.Method,
                                    model.RelativePath, inv.Line, entry.TestId));
                                continue;
                            }
                            noise.Add(new NoiseItem($"harness API: {inv.Receiver}.{inv.Method}",
                                model.RelativePath, inv.Line, entry.TestId));
                            continue;
                        }
                        AddLowered(lowered, entry.TestId, model.RelativePath, inv.Line);
                    }
                }
            }
        }

        // M7 coverage report v1 (TDS 7.4/15): the intent ratio. A SUT file counts as
        // "with intent" when it is the call site of at least one emitted event or
        // quarantined/unrepresentable candidate (tests reached it with behavioral
        // intent); the zero-intent complement is the actionable "add tests for X" list.
        var sutFileSet = new HashSet<string>(sutModels.Select(x => x.Model.RelativePath), StringComparer.Ordinal);
        var intentFiles = new HashSet<string>(StringComparer.Ordinal);
        void NoteIntent(IEnumerable<Provenance> provenance) { foreach (var p in provenance) if (sutFileSet.Contains(p.File)) intentFiles.Add(p.File); }
        foreach (var e in execs) NoteIntent(e.Provenance);
        foreach (var o in opens) NoteIntent(o.Provenance);
        foreach (var n in egress) NoteIntent(n.Provenance);
        foreach (var n in ingress) NoteIntent(n.Provenance);
        foreach (var q in quarantined) if (sutFileSet.Contains(q.File)) intentFiles.Add(q.File);
        foreach (var u in unrepresentable) if (sutFileSet.Contains(u.File)) intentFiles.Add(u.File);
        var untestedSutFiles = sutFileSet.Where(f => !intentFiles.Contains(f)).OrderBy(f => f, StringComparer.Ordinal).ToArray();

        var coverage = new CoverageReport(
            quarantined.OrderBy(q => q.File, StringComparer.Ordinal).ThenBy(q => q.Line).ThenBy(q => q.TestId, StringComparer.Ordinal).ToArray(),
            noise.OrderBy(n => n.File, StringComparer.Ordinal).ThenBy(n => n.Line).ThenBy(n => n.TestId, StringComparer.Ordinal).ToArray(),
            unrepresentable.OrderBy(u => u.File, StringComparer.Ordinal).ThenBy(u => u.Line).ThenBy(u => u.TestId, StringComparer.Ordinal).ToArray(),
            retired.OrderBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Line).ThenBy(r => r.TestId, StringComparer.Ordinal).ToArray(),
            mockShadowed.OrderBy(m => m.File, StringComparer.Ordinal).ThenBy(m => m.Line).ThenBy(m => m.TestId, StringComparer.Ordinal).ToArray(),
            untestedSutFiles,
            sutFiles.Count + testFiles.Count, testFiles.Count,
            testModels.Sum(t => t.Entries.Count),
            sutFileSet.Count, intentFiles.Count);

        var ir = new BehaviorIr(
            package, execs, opens, egress, ingress, coverage,
            grammarLock.ComputeLockHash(), configHash, config.Architectures);

        string yaml;
        try
        {
            yaml = CanonicalSbobEmitter.Emit(ir, options.GeneratorVersion, config.Namespace);
        }
        catch (EmitException) when (!options.Check)
        {
            // The refusal message says to inspect the coverage report (TDS 8.4) — write it
            // so there is something to inspect, then surface the refusal unchanged.
            var covPath = Path.Combine(options.RepoRoot, "sbob", $"{package.MetadataName}.coverage.json");
            Directory.CreateDirectory(Path.GetDirectoryName(covPath)!);
            File.WriteAllText(covPath, EmitCoverage(ir));
            Console.Error.WriteLine($"sbobforge: wrote {covPath} for inspection");
            throw;
        }
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

    internal static SourceLang LangOf(string file)
    {
        if (file.EndsWith(".java", StringComparison.OrdinalIgnoreCase)) return SourceLang.Java;
        if (file.EndsWith(".py", StringComparison.OrdinalIgnoreCase)) return SourceLang.Python;
        if (file.EndsWith(".go", StringComparison.OrdinalIgnoreCase)) return SourceLang.Go;
        if (file.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase)) return SourceLang.TypeScript;
        if (file.EndsWith(".php", StringComparison.OrdinalIgnoreCase)) return SourceLang.Php;
        if (file.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".psm1", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".psd1", StringComparison.OrdinalIgnoreCase)) return SourceLang.PowerShell;
        if (file.EndsWith(".sh", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".bash", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".bats", StringComparison.OrdinalIgnoreCase)) return SourceLang.Bash;
        if (file.EndsWith(".c", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".h", StringComparison.OrdinalIgnoreCase)) return SourceLang.C;
        if (file.EndsWith(".cc", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".cxx", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".hpp", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".hh", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".hxx", StringComparison.OrdinalIgnoreCase)) return SourceLang.Cpp;
        return SourceLang.CSharp;
    }

    internal static string LangKey(SourceLang lang) => lang switch
    {
        SourceLang.CSharp => "csharp",
        SourceLang.Java => "java",
        SourceLang.Python => "python",
        SourceLang.Go => "go",
        SourceLang.TypeScript => "typescript",
        SourceLang.Php => "php",
        SourceLang.PowerShell => "powershell",
        SourceLang.Bash => "bash",
        SourceLang.C => "c17",
        SourceLang.Cpp => "cpp20",
        _ => ""
    };

    /// <summary>Parser grammar-lock key for a language (TDS 5.3).</summary>
    internal static string ParserLockKey(SourceLang lang) => lang switch
    {
        SourceLang.Java => "java17",
        SourceLang.Python => "python311",
        SourceLang.Go => "go119",
        SourceLang.TypeScript => "typescript",
        SourceLang.Php => "php",
        SourceLang.PowerShell => "powershell",
        SourceLang.Bash => "bash",
        SourceLang.C => "c17",
        SourceLang.Cpp => "cpp20",
        _ => "csharp"
    };

    /// <summary>Catalog grammar-lock key for a language (the lowering table; TDS 5.3).</summary>
    internal static string CatalogLockKey(SourceLang lang) => lang switch
    {
        SourceLang.Java => JavaCatalog.LockEntryName,
        SourceLang.Python => PythonCatalog.LockEntryName,
        SourceLang.Go => GoCatalog.LockEntryName,
        SourceLang.TypeScript => JsCatalog.LockEntryName,
        SourceLang.Php => PhpCatalog.LockEntryName,
        SourceLang.PowerShell => PowerShellCatalog.LockEntryName,
        SourceLang.Bash => BashCatalog.LockEntryName,
        SourceLang.C => CCatalog.LockEntryName,
        SourceLang.Cpp => CppCatalog.LockEntryName,
        _ => DotNetCatalog.LockEntryName
    };

    internal static ICodeModelParser ParserFor(SourceLang lang) => lang switch
    {
        SourceLang.Java => new JavaSourceParser(),
        SourceLang.Python => new PythonSourceParser(),
        SourceLang.Go => new GoSourceParser(),
        SourceLang.TypeScript => new TypeScriptSourceParser(),
        SourceLang.Php => new PhpSourceParser(),
        SourceLang.PowerShell => new PowerShellSourceParser(),
        SourceLang.Bash => new BashSourceParser(),
        SourceLang.C => new C17SourceParser(),
        SourceLang.Cpp => new Cpp20SourceParser(),
        _ => new RoslynCSharpParser()
    };

    private static Func<CodeModel, IReadOnlyList<TestEntry>> RegistryFor(SourceLang lang) => lang switch
    {
        SourceLang.Java => new JavaTestFrameworkRegistry().ExtractEntries,
        SourceLang.Python => new PythonTestFrameworkRegistry().ExtractEntries,
        SourceLang.Go => new GoTestFrameworkRegistry().ExtractEntries,
        SourceLang.TypeScript => new JsTestFrameworkRegistry().ExtractEntries,
        SourceLang.Php => new PhpTestFrameworkRegistry().ExtractEntries,
        SourceLang.PowerShell => new PsTestFrameworkRegistry().ExtractEntries,
        SourceLang.Bash => new ShTestFrameworkRegistry().ExtractEntries,
        SourceLang.C => new CxxTestFrameworkRegistry().ExtractEntries,
        SourceLang.Cpp => new CxxTestFrameworkRegistry().ExtractEntries,
        _ => new TestFrameworkRegistry().ExtractEntries
    };

    internal static LoweredInvocation? LowerFor(SourceLang lang, InvocationDecl inv, Func<CallArg, CallArg?>? substitute) => lang switch
    {
        SourceLang.Java => JavaCatalogLowering.Lower(inv, substitute),
        SourceLang.Python => PythonCatalogLowering.Lower(inv, substitute),
        SourceLang.Go => GoCatalogLowering.Lower(inv, substitute),
        SourceLang.TypeScript => JsCatalogLowering.Lower(inv, substitute),
        SourceLang.Php => PhpCatalogLowering.Lower(inv, substitute),
        SourceLang.PowerShell => PowerShellCatalogLowering.Lower(inv, substitute),
        SourceLang.Bash => BashCatalogLowering.Lower(inv, substitute),
        SourceLang.C => CCatalogLowering.Lower(inv, substitute),
        SourceLang.Cpp => CppCatalogLowering.Lower(inv, substitute),
        _ => DotNetCatalogLowering.Lower(inv, substitute)
    };

    private static CodeModel ParseOrQuarantine(
        ICodeModelParser parser, string root, string file, HashSet<string> allowed, List<string> parseErrors)
    {
        // TDS 9 byte-identity, the family after #31: parsed source is the one
        // input the .gitattributes -text rules do NOT protect (they cover the
        // committed artifacts, not the .cs/.py/... trees a Windows checkout
        // hands the parsers with autocrlf line endings). Captured source
        // text — e.g. a multi-line receiver rendered into a harness-API
        // noise Description — then carries "\r\n" into coverage.json as an
        // escaped pair, which emit-side LF normalization cannot see (the CR
        // is already a two-character JSON escape, not a raw byte). Normalize
        // at the single parse-input choke point: every language, every
        // captured snippet. A no-op on Unix, so no committed golden moves.
        var text = CanonicalText.LfOnly(File.ReadAllText(Path.Combine(root, file)));
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

    private static (string Path, ResolutionTier Tier) ApplyExecPolicy(string target, SBOBForge.Core.Config.SbobForgeConfig config)
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

    private static string EmitCoverage(BehaviorIr ir) => CanonicalText.LfOnly(JsonSerializer.Serialize(new CoverageDocument(
        ir.IrVersion, ir.ComputeBehaviorHash(),
        ir.Coverage.FilesAnalyzed, ir.Coverage.TestFiles, ir.Coverage.TestEntries,
        ir.Coverage.SutFiles, ir.Coverage.FilesWithIntent,
        ir.Coverage.UntestedSutFiles.OrderBy(f => f, StringComparer.Ordinal).ToArray(),
        ir.Coverage.Quarantined.Select(q => new CoverageRow(
            q.Primitive.ToString(), q.Reason, q.File, q.Line, q.TestId, q.CatalogRuleId)).ToArray(),
        ir.Coverage.Noise.Select(n => new NoiseRow(n.Description, n.File, n.Line, n.TestId)).ToArray(),
        ir.Coverage.Unrepresentable.Select(u => new CoverageRow(
            "unrepresentable", u.Primitive, u.File, u.Line, u.TestId, "")).ToArray(),
        ir.Coverage.Retired.Select(r => new RetiredRow(r.TestId, r.File, r.Line)).ToArray(),
        ir.Coverage.MockShadowed.Select(m => new MockShadowRow(m.Kind, m.Detail, m.File, m.Line, m.TestId)).ToArray()), Json.Options)) + "\n";

    public static string RoslynVersion() =>
        typeof(Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private sealed record ProvenanceRow(
        string Primitive, string Identifier, string TestId, string File, int Line, string Tier, string Rule);

    private sealed record CoverageRow(
        string Primitive, string Reason, string File, int Line, string TestId, string Rule);

    private sealed record NoiseRow(string Description, string File, int Line, string TestId);

    private sealed record RetiredRow(string TestId, string File, int Line);

    private sealed record MockShadowRow(string Kind, string Detail, string File, int Line, string TestId);

    private sealed record CoverageDocument(
        int SbobIrVersion,
        string BehaviorHash,
        int FilesAnalyzed,
        int TestFiles,
        int TestEntries,
        int SutFiles,
        int FilesWithIntent,
        string[] UntestedSutFiles,
        CoverageRow[] Quarantined,
        NoiseRow[] Noise,
        CoverageRow[] Unrepresentable,
        RetiredRow[] Retired,
        MockShadowRow[] MockShadowed);
}
