# SBOB-Forge

**Tests to SBOB generator** — deterministically distill a package's test suite (xUnit, NUnit, MSTest, JUnit 5, TestNG, go test, pytest, PHPUnit, Jest, Vitest, Pester, Bats, GoogleTest, Catch2) into a signed Software Bill of Behavior (ContainerProfile, spec v0.0.4), bound to the package via a purl in spec.imageID.

- Design specification: [docs/TDS.md](docs/TDS.md) (v1.0, accepted 2026-10-31)
- Source standard: SBOB spec v0.0.4 — https://billofbehavior.com/bob/docs/spec/
- Downstream consumer: [DevelApp-ai/Software-Bill-of-Behavior](https://github.com/DevelApp-ai/Software-Bill-of-Behavior) (SBoB to Wazuh transpiler)
- Parsing substrate: [DevelApp.Minotaur](https://github.com/DevelApp-ai/Minotaur) + [Minotaur-Grammars](https://github.com/DevelApp-ai/Minotaur-Grammars)

## Status

**M2 Java + Python landed (2026-10-31):** built-in deterministic `java17` and `python311` parser backends (no external language toolchains), JUnit 5 / TestNG and pytest / unittest adapters, Java and Python behavior catalogs (pinned in the lock alongside the .NET catalog), per-language lowering (Java `FileOutputStream` append flags, `Runtime.exec` token split; Python `open()` mode strings, `subprocess` argv / `shell=True` → `/bin/sh -c`, `os.system`, URL / host-port / address-tuple network rules), parametrize / `@ValueSource` case propagation, and four committed golden fixtures (`examples/java-junit5-fixture`, `examples/java-testng-fixture`, `examples/python-pytest-fixture`, `examples/python-unittest-fixture`) drift-gated in CI next to the M1 .NET fixture.

**M1 core pipeline landed (2026-10-31):** grammar lock (`sbobforge.lock.json`, `--frozen`), deterministic ordinal discovery, Roslyn-backed C# parser with Minotaur 1.5.0 as the pinned source-validation substrate (see the implementation note in [TDS §5.2](docs/TDS.md)), xUnit/NUnit/MSTest adapters, .NET behavior catalog, behavior IR v1, canonical spec-v0.0.4 emitter with self-validation, `--check` drift gate, and a committed golden fixture (`examples/dotnet-xunit-fixture`) that regenerates byte-identically in CI. Remaining milestones per TDS §16; the dogfood profile targets a real DevelApp artifact.

### Try it

```
dotnet run --project src/SBOBForge.Cli -- version
dotnet run --project src/SBOBForge.Cli -- scan examples/python-pytest-fixture
for f in dotnet-xunit java-junit5 java-testng python-pytest python-unittest; do
  dotnet run --project src/SBOBForge.Cli -- generate "examples/$f-fixture" --check --frozen
done
```
