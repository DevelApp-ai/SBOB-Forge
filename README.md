# SBOB-Forge

**Tests to SBOB generator** — deterministically distill a package's test suite (xUnit, NUnit, MSTest, JUnit 5, TestNG, go test, pytest, PHPUnit, Jest, Vitest, Pester, Bats, GoogleTest, Catch2) into a signed Software Bill of Behavior (ContainerProfile, spec v0.0.4), bound to the package via a purl in spec.imageID.

- Design specification: [docs/TDS.md](docs/TDS.md) (v1.0, accepted 2026-10-31)
- Source standard: SBOB spec v0.0.4 — https://billofbehavior.com/bob/docs/spec/
- Downstream consumer: [DevelApp-ai/Software-Bill-of-Behavior](https://github.com/DevelApp-ai/Software-Bill-of-Behavior) (SBoB to Wazuh transpiler)
- Parsing substrate: [DevelApp.Minotaur](https://github.com/DevelApp-ai/Minotaur) + [Minotaur-Grammars](https://github.com/DevelApp-ai/Minotaur-Grammars)

## Status

Pre-M1. Design accepted 2026-10-31; implementation proceeds per the milestone plan in TDS section 16 (org decision #27: milestone issues close when capabilities are implemented).
