# SBOB-Forge

**Tests to SBOB generator** — deterministically distill a package's test suite (xUnit, NUnit, MSTest, JUnit 5, TestNG, go test, pytest, PHPUnit, Jest, Vitest, Pester, Bats, GoogleTest, Catch2) into a signed Software Bill of Behavior (ContainerProfile, spec v0.0.4), bound to the package via a purl in spec.imageID.

- Design specification: [docs/TDS.md](docs/TDS.md) (v1.0, accepted 2026-10-31)
- Source standard: SBOB spec v0.0.4 — https://billofbehavior.com/bob/docs/spec/
- Downstream consumer: [DevelApp-ai/Software-Bill-of-Behavior](https://github.com/DevelApp-ai/Software-Bill-of-Behavior) (SBoB to Wazuh transpiler)
- Parsing substrate: [DevelApp.Minotaur](https://github.com/DevelApp-ai/Minotaur) + [Minotaur-Grammars](https://github.com/DevelApp-ai/Minotaur-Grammars)
- **Third-party adoption guide**: [docs/adoption.md](docs/adoption.md) — bring your own registry, two installs, nothing private from DevelApp
- **GitHub Action**: root [`action.yml`](action.yml) wraps the sbobforge CLI; the full signed publish lane is the reusable [`sbob-registry-publish.yml`](.github/workflows/sbob-registry-publish.yml); starter workflow template in [`.github/workflow-templates/`](.github/workflow-templates/)

## Status

**M5 C/C++ landed (2026-11-01):** built-in deterministic `c17` and `cpp20` structural parser backends (shared core: preprocessor-line tokens, raw strings, namespace/class/function model, test-macro blocks, socket-field hoisting for `htons`/`inet_addr` assignments), GoogleTest / Catch2 / Unity adapters (`TEST*`/`TEST_CASE*` macros → `cxx.entry` methods with per-instance names; `GTEST_SKIP`/`SKIP`/`TEST_IGNORE` disable cues; Unity entries only via `RUN_TEST` registration), and `c-catalog` / `cpp-catalog` lowering (`execve`/`execvp` argv masks with argv0-anchoring, `system`/`popen` shell forms, `fopen` mode table, POSIX `open(2)` `O_*` masks, `getaddrinfo` egress, BSD/C++ `connect`/`listen`, `std::ifstream`/`ofstream`/`fstream` mode lowering, `boost::asio` resolver egress, `unlink`/`remove_all` deletes). Two committed golden fixtures (`examples/c-googletest-fixture`, `examples/cpp-catch2-fixture`) drift-gate in CI next to the earlier ones; all fixture locks pin `c17`/`cpp20` and the two new catalogs. See the implementation note in [TDS §5.2](docs/TDS.md).

**M5 Phase B landed (2026-11-01):** the `powershell` and `bash` grammars authored in Minotaur-Grammars (PR #196) are now sha256-pinned in every fixture lock via byte-identical embedded copies (verifiable offline under `--frozen`), and the M4 lexical scanners are upgraded to structural statement parsers (`sbobforge-powershell` / `sbobforge-bash` v2.0.0): PowerShell functions/classes/enums, `param(…)` blocks as modeled parameter lists, full control flow (`if`/`switch`/`foreach`/`try`), here-strings; Bash recursive function definitions, `if`/`case`/`while`/`for`, heredocs, fd-redirect normalization. The `parser: lexical` reduced-confidence markers are retired from the coverage report and SBOB annotations, and the Minotaur source-validation substrate is pinned to 1.5.25 from the GitHub Packages feed. See the Phase B note in [TDS §5.4](docs/TDS.md).

**M4 PHP + PowerShell/Bash landed (2026-11-01):** structural `php` parser backend (namespaces, classes, docblock `@test`/`@requires` and `#[Test]` entry cues, `markTestSkipped` disable), PHPUnit adapter, `php-catalog` lowering (fopen mode table, dual-purpose `file_get_contents` open/URL kind, literal-first-token `exec` family, `curl_init` egress), plus the TDS §5.4 Phase A lexical adapters: `powershell` and `bash` fixed token scanners with Pester v5 / Bats adapters and `powershell-catalog` / `bash-catalog` (command words, `-Param` named args, `&` call operator, `Start-Process`, `Invoke-WebRequest`/`curl`, `nc`/`ssh`, redirection opens, `Invoke-Expression` RCE-shaped quarantine). Lexical backends are marked `parser: lexical` in the coverage report and SBOB annotations (reduced confidence tier), and three committed golden fixtures (`examples/php-phpunit-fixture`, `examples/powershell-pester-fixture`, `examples/bash-bats-fixture`) drift-gate in CI next to the earlier ones. See the implementation note in [TDS §5.4](docs/TDS.md).

**M3 Go + JS/TS landed (2026-11-01):** built-in deterministic `go119` and `typescript` parser backends (one backend covers `.js/.mjs/.cjs/.ts/.tsx/.jsx`), go test / Jest / Vitest adapters, Go and JS behavior catalogs (pinned in the lock as `go-catalog` / `js-catalog`), per-language lowering (Go `os.OpenFile` flag masks → canonical spec flags, `net.Dial`/`net.Listen` literal addr forms; JS `child_process` shell vs argv forms with argv0-anchoring, `fs` open mode strings, bare-receiver `fetch`/`execFile` vs `RegExp.exec` exclusion, any-receiver `listen`), Jest `.each` case propagation with callback-parameter substitution, `xdescribe`/`describe.skip` describe-level skip resolution, and three committed golden fixtures (`examples/go-testing-fixture`, `examples/js-jest-fixture`, `examples/ts-vitest-fixture`) drift-gated in CI next to the M1/M2 fixtures. See the implementation note in [TDS §5.2](docs/TDS.md).

**M2 Java + Python landed (2026-10-31):** built-in deterministic `java17` and `python311` parser backends (no external language toolchains), JUnit 5 / TestNG and pytest / unittest adapters, Java and Python behavior catalogs (pinned in the lock alongside the .NET catalog), per-language lowering (Java `FileOutputStream` append flags, `Runtime.exec` token split; Python `open()` mode strings, `subprocess` argv / `shell=True` → `/bin/sh -c`, `os.system`, URL / host-port / address-tuple network rules), parametrize / `@ValueSource` case propagation, and four committed golden fixtures (`examples/java-junit5-fixture`, `examples/java-testng-fixture`, `examples/python-pytest-fixture`, `examples/python-unittest-fixture`) drift-gated in CI next to the M1 .NET fixture.

**M1 core pipeline landed (2026-10-31):** grammar lock (`sbobforge.lock.json`, `--frozen`), deterministic ordinal discovery, Roslyn-backed C# parser with Minotaur 1.5.0 as the pinned source-validation substrate (see the implementation note in [TDS §5.2](docs/TDS.md)), xUnit/NUnit/MSTest adapters, .NET behavior catalog, behavior IR v1, canonical spec-v0.0.4 emitter with self-validation, `--check` drift gate, and a committed golden fixture (`examples/dotnet-xunit-fixture`) that regenerates byte-identically in CI. Remaining milestones per TDS §16; the dogfood profile targets a real DevelApp artifact.

### Try it

```
dotnet run --project src/SBOBForge.Cli -- version
dotnet run --project src/SBOBForge.Cli -- scan examples/python-pytest-fixture
for f in dotnet-xunit java-junit5 java-testng python-pytest python-unittest go-testing js-jest ts-vitest php-phpunit powershell-pester bash-bats; do
  dotnet run --project src/SBOBForge.Cli -- generate "examples/$f-fixture" --check --frozen
done
```

## License

MIT — see [LICENSE](LICENSE). Copyright (c) 2026 DevelApp.ai ApS.
