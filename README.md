# SBOB-Forge

**Tests to SBOB generator** — deterministically distill a package's test suite (xUnit, NUnit, MSTest, JUnit 5, TestNG, go test, pytest, PHPUnit, Jest, Vitest, Pester, Bats, GoogleTest, Catch2) into a signed Software Bill of Behavior (ContainerProfile, spec v0.0.4), bound to the package via a purl in spec.imageID.

- Design specification: [docs/TDS.md](docs/TDS.md) (v1.0, accepted 2026-10-31)
- Source standard: SBOB spec v0.0.4 — https://billofbehavior.com/bob/docs/spec/
- Downstream consumer: [DevelApp-ai/Software-Bill-of-Behavior](https://github.com/DevelApp-ai/Software-Bill-of-Behavior) (SBoB to Wazuh transpiler)
- Parsing substrate: [DevelApp.Minotaur](https://github.com/DevelApp-ai/Minotaur) + [Minotaur-Grammars](https://github.com/DevelApp-ai/Minotaur-Grammars)

## Status

**M1 core pipeline landed (2026-10-31):** grammar lock (`sbobforge.lock.json`, `--frozen`), deterministic ordinal discovery, Roslyn-backed C# parser with Minotaur 1.5.0 as the pinned source-validation substrate (see the implementation note in [TDS §5.2](docs/TDS.md)), xUnit/NUnit/MSTest adapters, .NET behavior catalog, behavior IR v1, canonical spec-v0.0.4 emitter with self-validation, `--check` drift gate, and a committed golden fixture (`examples/dotnet-xunit-fixture`) that regenerates byte-identically in CI. Dogfooding over DevelApp.Minotaur's real suite and remaining milestones continue per TDS §16 (issue #4 stays open until the dogfood target lands).

### Try it

```
dotnet run --project src/SBOBForge.Cli -- version
dotnet run --project src/SBOBForge.Cli -- scan examples/dotnet-xunit-fixture
dotnet run --project src/SBOBForge.Cli -- generate examples/dotnet-xunit-fixture --check --frozen
```
