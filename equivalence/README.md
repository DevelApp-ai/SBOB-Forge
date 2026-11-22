# Cross-language equivalence corpus (TDS 14.3, SBOB-Forge #6)

The same behavioral scenario expressed in every language that can natively
express it must yield **byte-identical emitted SBoBs**. Any divergence in
catalogs, parsers, or adapters shows up as a behavioral inequality between
languages, not just a broken golden.

## Layout

- `scenarios/<scenario>/sbobforge.json` — one shared, all-literal config per
  scenario (no policies except `execPathPolicy` where the scenario primitive
  requires a bare-name mapping, e.g. `git`).
- `scenarios/<scenario>/<lang>/sut` + `/<lang>/tests` — per-language mini-repo
  exercising the scenario with each language's primary framework
  (csharp/xunit, java/junit5, python/pytest, go/testing, js/jest, ts/vitest,
  php/phpunit, powershell/pester, bash/bats, c/googletest, cpp/catch2).

The harness (`src/SBOBForge.Tests/EquivalenceTests.cs`) materializes every
language variant into a temp repo (plus the canonical fixture lock), runs the
full generate pipeline, and asserts:

1. `AllLanguagesEmitByteIdenticalSbobs` — every language's `sbobforge generate`
   output is byte-identical within the scenario (metadata, annotations,
   behavior-hash and all spec sections).
2. `SectionsMatchTheScenarioContract` — each language's IR matches the
   scenario's explicit expected sections (execs/opens/egress/ingress) and
   coverage contract, guarding against all-language drift.

## Participation is per scenario

A language joins a scenario only when its catalog has a **native**
(non-shell-mediated) rule for the scenario primitive. Honest exclusions are
recorded in each scenario's `README.md`; they are never forced into the corpus:

- **open-read** excludes bash: the bash catalog has no read-redirect rule
  (only redir-truncate/append); `cat`/`<` are not cataloged opens.
- **exec-args** excludes cpp: the C++ catalog's only exec rules
  (`std::system`/`popen`) are shell-mediated (`/bin/sh -c`), which emits a
  different exec section by design.
- **listen-ingress** excludes bash, php, powershell: no ingress catalog rule
  exists for these languages (bash `nc -l` is egress-shaped, PowerShell's
  `TcpListener` shapes are not cataloged, php has no listen rule).

## Status

7 core scenarios landed (open-read, open-append, open-write-truncate,
exec-args, https-egress, listen-ingress, dynamic-open-quarantine) out of the
~20 tracked in SBOB-Forge #6. Adding a scenario = new directory + contract
case in the harness; no other code changes.
