using System;
using System.IO;
using System.Text;
using SBOBForge.Catalog;
using SBOBForge.Core;
using SBOBForge.Core.Config;
using SBOBForge.Core.Emit;
using SBOBForge.Parsing;

namespace SBOBForge.Cli;

/// <summary>
/// sbobforge — deterministic test-suite → SBOB generator (TDS 13).
/// Exit codes: 0 ok, 1 usage, 2 parse/validation hard error, 3 --check drift,
/// 4 --strict quarantine, 5 --frozen lock mismatch.
/// </summary>
public static class Program
{
    public const string Version = "0.1.0";

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            return Run(args);
        }
        catch (ParseHardError e) { return Fail(2, e.Message); }
        catch (EmitException e) { return Fail(2, e.Message); }
        catch (DriftDetectedException e) { return Fail(3, e.Message); }
        catch (StrictQuarantineException e) { return Fail(4, e.Message); }
        catch (LockMismatchException e) { return Fail(5, e.Message); }
        catch (InvalidDataException e) { return Fail(2, e.Message); }
        catch (FormatException e) { return Fail(2, e.Message); }
    }

    private static int Fail(int code, string message)
    {
        Console.Error.WriteLine("sbobforge: " + message);
        return code;
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0) return Usage("missing command");
        var command = args[0];
        try
        {
            return command switch
            {
                "version" => CmdVersion(),
                "lock" => CmdLock(args[1..]),
                "scan" => CmdScan(args[1..]),
                "generate" => CmdGenerate(args[1..]),
                "validate" => CmdValidate(args[1..]),
                _ => Usage($"unknown command '{command}'")
            };
        }
        catch (IndexOutOfRangeException)
        {
            return Usage($"missing argument for '{command}'");
        }
    }

    private static int Usage(string problem)
    {
        Console.Error.WriteLine("sbobforge: " + problem);
        Console.Error.WriteLine("""
            usage:
              sbobforge version
              sbobforge lock    <repoRoot> [--update]
              sbobforge scan    <repoRoot> [--config <path>]
              sbobforge generate <repoRoot> [--config <path>] [--lock <path>]
                                [--target-os <os>] [--check] [--strict] [--frozen]
              sbobforge validate <file.sbob.yaml>

            exit codes: 0 ok, 1 usage, 2 parse/validation error, 3 drift (--check),
                        4 quarantine (--strict), 5 lock mismatch (--frozen)
            """);
        return 1;
    }

    private static int CmdVersion()
    {
        Console.WriteLine($"sbobforge {Version} (IR v{Core.Model.BehaviorIr.CurrentVersion})");
        Console.WriteLine($"  parser backends: roslyn-csharp {GeneratePipeline.RoslynVersion()}, " +
                          $"sbobforge-java17 {Parsing.JavaSourceParser.Version}, " +
                          $"sbobforge-python311 {Parsing.PythonSourceParser.Version}, " +
                          $"sbobforge-go119 {Parsing.GoSourceParser.Version}, " +
                          $"sbobforge-typescript {Parsing.TypeScriptSourceParser.Version}, " +
                          $"sbobforge-php {Parsing.PhpSourceParser.Version}, " +
                          $"sbobforge-powershell {Parsing.PowerShellSourceParser.Version}, " +
                          $"sbobforge-bash {Parsing.BashSourceParser.Version}, " +
                          $"sbobforge-c17 {Parsing.C17SourceParser.Version}, " +
                          $"sbobforge-cpp20 {Parsing.Cpp20SourceParser.Version} (pinned via lock; " +
                          "powershell/bash are lexical — TDS 5.4)");
        Console.WriteLine($"  source-validation substrate: DevelApp.Minotaur {MinotaurSourceValidator.InstalledVersion()} (pinned via lock)");
        Console.WriteLine($"  catalogs: dotnet v{DotNetCatalogLowering.Catalog.Version}, " +
                          $"java v{JavaCatalogLowering.Catalog.Version}, " +
                          $"python v{PythonCatalogLowering.Catalog.Version}, " +
                          $"go v{GoCatalogLowering.Catalog.Version}, " +
                          $"js v{JsCatalogLowering.Catalog.Version}, " +
                          $"php v{PhpCatalogLowering.Catalog.Version}, " +
                          $"powershell v{PowerShellCatalogLowering.Catalog.Version}, " +
                          $"bash v{BashCatalogLowering.Catalog.Version}, " +
                          $"c v{CCatalogLowering.Catalog.Version}, " +
                          $"cpp v{CppCatalogLowering.Catalog.Version}");
        return 0;
    }

    private static int CmdLock(string[] args)
    {
        var root = Path.GetFullPath(args[0]);
        string? explicitPath = null;
        var update = false;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--update") update = true;
            else if (args[i] == "--config" && i + 1 < args.Length) explicitPath = args[++i];
        }

        var lockPath = Path.Combine(root, "sbobforge.lock.json");
        var hasExisting = File.Exists(lockPath);
        if (hasExisting && !update)
        {
            var existing = GrammarLock.Load(lockPath);
            Console.WriteLine($"lock: {lockPath} (minotaur {existing.Minotaur.Version}, csharp {GrammarCsharp(existing)})");
            Console.WriteLine("  hash: " + existing.ComputeLockHash());
            return 0;
        }

        var lockModel = hasExisting ? GrammarLock.Load(lockPath) : new GrammarLock();
        lockModel.Minotaur.Version = MinotaurSourceValidator.InstalledVersion();
        lockModel.Grammars["csharp"] = new GrammarLockEntry
        {
            Version = GeneratePipeline.RoslynVersion(),
            Sha256 = ""
        };
        lockModel.Grammars["java17"] = new GrammarLockEntry
        {
            Version = Parsing.JavaSourceParser.Version,
            Sha256 = ""
        };
        lockModel.Grammars["python311"] = new GrammarLockEntry
        {
            Version = Parsing.PythonSourceParser.Version,
            Sha256 = ""
        };
        lockModel.Grammars["go119"] = new GrammarLockEntry
        {
            Version = Parsing.GoSourceParser.Version,
            Sha256 = ""
        };
        lockModel.Grammars["typescript"] = new GrammarLockEntry
        {
            Version = Parsing.TypeScriptSourceParser.Version,
            Sha256 = ""
        };
        lockModel.Grammars["php"] = new GrammarLockEntry
        {
            Version = Parsing.PhpSourceParser.Version,
            Sha256 = ""
        };
        lockModel.Grammars["powershell"] = new GrammarLockEntry
        {
            Version = Parsing.PowerShellSourceParser.Version,
            Sha256 = ""
        };
        lockModel.Grammars["bash"] = new GrammarLockEntry
        {
            Version = Parsing.BashSourceParser.Version,
            Sha256 = ""
        };
        lockModel.Grammars["c17"] = new GrammarLockEntry
        {
            Version = Parsing.C17SourceParser.Version,
            Sha256 = ""
        };
        lockModel.Grammars["cpp20"] = new GrammarLockEntry
        {
            Version = Parsing.Cpp20SourceParser.Version,
            Sha256 = ""
        };
        lockModel.Grammars[DotNetCatalog.LockEntryName] = new GrammarLockEntry
        {
            Version = DotNetCatalogLowering.Catalog.Version.ToString(),
            Sha256 = DotNetCatalog.ComputeDefaultHash()
        };
        lockModel.Grammars[JavaCatalog.LockEntryName] = new GrammarLockEntry
        {
            Version = JavaCatalogLowering.Catalog.Version.ToString(),
            Sha256 = JavaCatalog.ComputeDefaultHash()
        };
        lockModel.Grammars[PythonCatalog.LockEntryName] = new GrammarLockEntry
        {
            Version = PythonCatalogLowering.Catalog.Version.ToString(),
            Sha256 = PythonCatalog.ComputeDefaultHash()
        };
        lockModel.Grammars[GoCatalog.LockEntryName] = new GrammarLockEntry
        {
            Version = GoCatalogLowering.Catalog.Version.ToString(),
            Sha256 = GoCatalog.ComputeDefaultHash()
        };
        lockModel.Grammars[JsCatalog.LockEntryName] = new GrammarLockEntry
        {
            Version = JsCatalogLowering.Catalog.Version.ToString(),
            Sha256 = JsCatalog.ComputeDefaultHash()
        };
        lockModel.Grammars[PhpCatalog.LockEntryName] = new GrammarLockEntry
        {
            Version = PhpCatalogLowering.Catalog.Version.ToString(),
            Sha256 = PhpCatalog.ComputeDefaultHash()
        };
        lockModel.Grammars[PowerShellCatalog.LockEntryName] = new GrammarLockEntry
        {
            Version = PowerShellCatalogLowering.Catalog.Version.ToString(),
            Sha256 = PowerShellCatalog.ComputeDefaultHash()
        };
        lockModel.Grammars[BashCatalog.LockEntryName] = new GrammarLockEntry
        {
            Version = BashCatalogLowering.Catalog.Version.ToString(),
            Sha256 = BashCatalog.ComputeDefaultHash()
        };
        lockModel.Grammars[CCatalog.LockEntryName] = new GrammarLockEntry
        {
            Version = CCatalogLowering.Catalog.Version.ToString(),
            Sha256 = CCatalog.ComputeDefaultHash()
        };
        lockModel.Grammars[CppCatalog.LockEntryName] = new GrammarLockEntry
        {
            Version = CppCatalogLowering.Catalog.Version.ToString(),
            Sha256 = CppCatalog.ComputeDefaultHash()
        };
        lockModel.Save(lockPath);
        Console.WriteLine($"{(update ? "updated" : "created")} {lockPath}");
        Console.WriteLine("  minotaur " + lockModel.Minotaur.Version);
        Console.WriteLine("  csharp " + GrammarCsharp(lockModel) +
                          ", java17 " + lockModel.Grammars["java17"].Version +
                          ", python311 " + lockModel.Grammars["python311"].Version +
                          ", go119 " + lockModel.Grammars["go119"].Version +
                          ", typescript " + lockModel.Grammars["typescript"].Version +
                          ", php " + lockModel.Grammars["php"].Version +
                          $", powershell " + lockModel.Grammars["powershell"].Version +
                          ", bash " + lockModel.Grammars["bash"].Version +
                          ", c17 " + lockModel.Grammars["c17"].Version +
                          ", cpp20 " + lockModel.Grammars["cpp20"].Version);
        Console.WriteLine("  dotnet-catalog v" + DotNetCatalogLowering.Catalog.Version + " sha256 " + lockModel.Grammars[DotNetCatalog.LockEntryName].Sha256);
        Console.WriteLine("  java-catalog v" + JavaCatalogLowering.Catalog.Version + " sha256 " + lockModel.Grammars[JavaCatalog.LockEntryName].Sha256);
        Console.WriteLine("  python-catalog v" + PythonCatalogLowering.Catalog.Version + " sha256 " + lockModel.Grammars[PythonCatalog.LockEntryName].Sha256);
        Console.WriteLine("  go-catalog v" + GoCatalogLowering.Catalog.Version + " sha256 " + lockModel.Grammars[GoCatalog.LockEntryName].Sha256);
        Console.WriteLine("  js-catalog v" + JsCatalogLowering.Catalog.Version + " sha256 " + lockModel.Grammars[JsCatalog.LockEntryName].Sha256);
        Console.WriteLine("  php-catalog v" + PhpCatalogLowering.Catalog.Version + " sha256 " + lockModel.Grammars[PhpCatalog.LockEntryName].Sha256);
        Console.WriteLine("  powershell-catalog v" + PowerShellCatalogLowering.Catalog.Version + " sha256 " + lockModel.Grammars[PowerShellCatalog.LockEntryName].Sha256);
        Console.WriteLine("  bash-catalog v" + BashCatalogLowering.Catalog.Version + " sha256 " + lockModel.Grammars[BashCatalog.LockEntryName].Sha256);
        Console.WriteLine("  c-catalog v" + CCatalogLowering.Catalog.Version + " sha256 " + lockModel.Grammars[CCatalog.LockEntryName].Sha256);
        Console.WriteLine("  cpp-catalog v" + CppCatalogLowering.Catalog.Version + " sha256 " + lockModel.Grammars[CppCatalog.LockEntryName].Sha256);
        Console.WriteLine("  hash: " + lockModel.ComputeLockHash());
        Console.WriteLine("  review lock changes like a dependency bump (TDS 5.3)");
        return 0;
    }

    private static string GrammarCsharp(GrammarLock l) =>
        l.Grammars.TryGetValue("csharp", out var g) ? g.Version : "<unpinned>";

    private static int CmdScan(string[] args)
    {
        var root = Path.GetFullPath(args[0]);
        var configPath = OptionalPath(args, root);
        var config = SbobForgeConfig.Load(configPath ?? Path.Combine(root, "sbobforge.json"));
        var parsers = new Dictionary<string, Parsing.ICodeModelParser>();

        Parsing.ICodeModelParser ParserFor(string file)
        {
            var key = Path.GetExtension(file).ToLowerInvariant();
            if (!parsers.TryGetValue(key, out var parser))
            {
                parser = key switch
                {
                    ".java" => new Parsing.JavaSourceParser(),
                    ".py" => new Parsing.PythonSourceParser(),
                    ".go" => new Parsing.GoSourceParser(),
                    ".js" or ".mjs" or ".cjs" or ".ts" or ".tsx" or ".jsx" => new Parsing.TypeScriptSourceParser(),
                    ".php" => new Parsing.PhpSourceParser(),
                    ".ps1" or ".psm1" or ".psd1" => new Parsing.PowerShellSourceParser(),
                    ".sh" or ".bash" or ".bats" => new Parsing.BashSourceParser(),
                    ".c" or ".h" => new Parsing.C17SourceParser(),
                    ".cc" or ".cpp" or ".cxx" or ".hpp" or ".hh" or ".hxx" => new Parsing.Cpp20SourceParser(),
                    _ => new RoslynCSharpParser()
                };
                parsers[key] = parser;
            }
            return parser;
        }

        Func<Parsing.CodeModel, IReadOnlyList<Adapters.TestEntry>> RegistryFor(string file) =>
            Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".java" => new Adapters.Java.JavaTestFrameworkRegistry().ExtractEntries,
                ".py" => new Adapters.Python.PythonTestFrameworkRegistry().ExtractEntries,
                ".go" => new Adapters.Go.GoTestFrameworkRegistry().ExtractEntries,
                ".js" or ".mjs" or ".cjs" or ".ts" or ".tsx" or ".jsx" =>
                    new Adapters.TypeScript.JsTestFrameworkRegistry().ExtractEntries,
                ".php" => new Adapters.Php.PhpTestFrameworkRegistry().ExtractEntries,
                ".ps1" or ".psm1" or ".psd1" => new Adapters.PowerShell.PsTestFrameworkRegistry().ExtractEntries,
                ".sh" or ".bash" or ".bats" => new Adapters.Bash.ShTestFrameworkRegistry().ExtractEntries,
                ".c" or ".h" or ".cc" or ".cpp" or ".cxx" or ".hpp" or ".hh" or ".hxx" =>
                    new Adapters.Cxx.CxxTestFrameworkRegistry().ExtractEntries,
                _ => new Adapters.DotNet.TestFrameworkRegistry().ExtractEntries
            };

        foreach (var file in Core.Discovery.FileDiscovery.Enumerate(root, config.SutGlobs))
        {
            var model = ParserFor(file).Parse(file, File.ReadAllText(Path.Combine(root, file)));
            var methods = model.Types.Count == 0 ? 0 : model.AllMethods.Count();
            Console.WriteLine($"sut   {file} ({model.Types.Count} types, {methods} methods)");
        }
        foreach (var file in Core.Discovery.FileDiscovery.Enumerate(root, config.TestGlobs))
        {
            var model = ParserFor(file).Parse(file, File.ReadAllText(Path.Combine(root, file)));
            var entries = RegistryFor(file)(model);
            Console.WriteLine($"test  {file} {entries.Count} entries");
            foreach (var e in entries)
                Console.WriteLine($"        {e.TestId} {(e.Enabled ? "" : " (disabled)")} @{e.File}:{e.Line}");
        }
        return 0;
    }

    private static int CmdGenerate(string[] args)
    {
        var root = Path.GetFullPath(args[0]);
        string? configPath = null, lockPath = null, targetOs = null;
        var check = false; var strict = false; var frozen = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config" when i + 1 < args.Length: configPath = args[++i]; break;
                case "--lock" when i + 1 < args.Length: lockPath = args[++i]; break;
                case "--target-os" when i + 1 < args.Length: targetOs = args[++i]; break;
                case "--check": check = true; break;
                case "--strict": strict = true; break;
                case "--frozen": frozen = true; break;
                default: return Usage($"unknown option '{args[i]}'");
            }
        }

        var result = GeneratePipeline.Run(new GenerationOptions
        {
            RepoRoot = root,
            ConfigPath = configPath ?? Path.Combine(root, "sbobforge.json"),
            LockPath = lockPath ?? Path.Combine(root, "sbobforge.lock.json"),
            TargetOsOverride = targetOs,
            Strict = strict,
            Frozen = frozen,
            Check = check,
            GeneratorVersion = Version
        });

        var files = new (string Name, string Content)[]
        {
            (result.SbobFileName, result.SbobYaml),
            (result.ProvenanceFileName, result.ProvenanceJsonl),
            (result.CoverageFileName, result.CoverageJson)
        };

        if (check)
        {
            foreach (var (name, content) in files)
            {
                var full = Path.Combine(root, name);
                if (!File.Exists(full) || File.ReadAllText(full) != content)
                    throw new DriftDetectedException(
                        $"drift: '{name}' differs from what the current code would generate. " +
                        "Regenerate and commit (org decision #26: generated artifacts are committed and drift-gated).");
            }
            Console.WriteLine($"check: no drift ({result.Ir.Execs.Count} execs, {result.Ir.Opens.Count} opens, " +
                              $"{result.Ir.Egress.Count} egress, {result.Ir.Ingress.Count} ingress; " +
                              $"behavior-hash {result.Ir.ComputeBehaviorHash()[..12]})");
            return 0;
        }

        foreach (var (name, content) in files)
        {
            var full = Path.Combine(root, name);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            Console.WriteLine($"wrote {name}");
        }
        Console.WriteLine($"behavior-hash {result.Ir.ComputeBehaviorHash()[..12]} " +
                          $"({result.Ir.Execs.Count} execs, {result.Ir.Opens.Count} opens, " +
                          $"{result.Ir.Egress.Count} egress, {result.Ir.Ingress.Count} ingress; " +
                          $"{result.Ir.Coverage.Quarantined.Count} quarantined)");
        return 0;
    }

    private static int CmdValidate(string[] args)
    {
        var path = args[0];
        if (!File.Exists(path)) return Fail(1, $"no such file: {path}");
        var yaml = File.ReadAllText(path);
        var errors = Core.Validation.SbobValidator.Validate(yaml);
        if (errors.Count == 0)
        {
            Console.WriteLine("valid ContainerProfile (spdx.softwarecomposition.kubescape.io/v1beta1 + purl extension)");
            return 0;
        }
        foreach (var e in errors) Console.Error.WriteLine(e);
        return 2;
    }

    private static string? OptionalPath(string[] args, string root)
    {
        for (var i = 1; i < args.Length; i++)
            if (args[i] == "--config" && i + 1 < args.Length)
                return Path.GetFullPath(args[i + 1], root);
        return null;
    }
}
