# Adopting SBoB on GitHub — bring your own registry

This guide takes a repository from zero to **published, signed, verified SBoB
profiles in a registry YOU own**, using two public tooling repos and nothing
private from DevelApp:

| Role | Repo | What you install |
|---|---|---|
| **Generate + publish** | [DevelApp-ai/SBOB-Forge](https://github.com/DevelApp-ai/SBOB-Forge) | root `action.yml` (CLI wrapper) or the `sbob-registry-publish.yml` reusable workflow |
| **Validate + diff + enforce** | [DevelApp-ai/Software-Bill-of-Behavior](https://github.com/DevelApp-ai/Software-Bill-of-Behavior) | `sbobctl` composite action (`sbobctl/action.yml`) or `node src/cli.ts` on Node ≥ 22 |

The aimed symmetry: **SBOB-Forge = generate/publish, Software-Bill-of-Behavior
= validate/diff/enforce, your registry repo = data + signatures only.**
Neither repo needs write access to the other; the only cross-repo write in
the whole pipeline is the registry PR you authorize with your own token.

## 1. Generate a profile from your test suite

SBOB-Forge derives behavior statically from a **committed test corpus** —
if your suite is thin, extend it first; a profile is only as honest as the
tests behind it. Supported frameworks: xUnit/NUnit/MSTest, JUnit 5/TestNG,
pytest/unittest, go test, Jest/Vitest, PHPUnit, Pester, Bats, GoogleTest,
Catch2, Unity.

```sh
dotnet run --project src/SBOBForge.Cli -- generate <your-project-dir>
dotnet run --project src/SBOBForge.Cli -- generate <your-project-dir> --check --frozen   # drift gate
```

In CI (composite action, .NET 8):

```yaml
      - uses: DevelApp-ai/SBOB-Forge@<sha>
        with:
          command: generate
          args: ". --check --frozen"
```

Commit the generated `sbob/` artifacts; the drift gate must regenerate them
byte-identically on every run (profiles are committed, never repaired
ad-hoc).

> **Restore note:** the CLI pins its parsing substrate
> (`DevelApp.Minotaur`) through a GitHub Packages feed — forward a
> `read:packages` PAT (`nuget-auth-token` input / `NUGET_AUTH_TOKEN`) until
> the substrate is additionally published on nuget.org.

## 2. Stand up your registry repo

Any repo you own; the layout contract is:

```
sbobs/
  <ecosystem>/            # nuget, choco, npm, pypi, deb, ... — you pick the lane
    <name>.yaml           # the registry-dialect SBoB entry
    <name>.yaml.sig       # detached cosign signature
    <name>.yaml.bundle    # cosign Rekor bundle
```

Registry repos carry **data and signatures only** — no tooling, no product
code. The pipeline opens version-bump PRs; it never merges them.

## 3. The PAT (read this once, save an evening)

The registry PR is a **cross-repo write**; the calling workflow's
`GITHUB_TOKEN` can never open it, and the pipeline **fails closed on an
empty token** instead of falling back. Create a fine-grained PAT:

- **Repository access:** exactly your registry repo
- **Permissions:** Contents read/write, **Pull requests read/write**, Metadata read

(The Pull-requests permission is not optional — a Contents-only PAT can
push branches but not open the registry PR. This cost us a production
publish cycle once; the failure mode is now explicit.)

## 4. Publish (reusable workflow)

```yaml
jobs:
  sbob-publish:
    uses: DevelApp-ai/SBOB-Forge/.github/workflows/sbob-registry-publish.yml@<sha>   # pin by SHA
    with:
      package-purl: "pkg:nuget/your-package@1.2.3"     # the package this SBoB describes
      sbob-file: "sbob/your-package.linux.sbob.yaml"   # committed, drift-gated profile
      registry-dir: "sbobs/nuget"                      # ecosystem lane in the registry
      registry-repo: "your-org/your-sbob-registry"    # YOUR registry
      lane: "public"                                   # public enforces the content gate
    permissions:
      contents: read
      packages: read
      id-token: write                                  # cosign keyless OIDC
    secrets:
      SBOB_REGISTRY_TOKEN: ${{ secrets.SBOB_REGISTRY_TOKEN }}     # the PAT from step 3
      PACKAGES_READ_TOKEN: ${{ secrets.PACKAGES_READ_TOKEN }}     # read:packages, or drop
```

The lane runs, in order: **drift gate → dialect convert → cosign keyless
sign → content gate → sbobctl validate/diff → registry PR.** Signing
happens **after conversion, over the exact registry bytes** — the Rekor
entry commits the sha256 of the file you publish.

`lane: public` enforces the content gate: profiles naming private DNS
suffixes, RFC1918/link-local/ULA/CGNAT addresses, or bare single-label
hostnames fail **before** the PR opens (loopback allowed; `--allow`-style
overrides exist per-entry). Use `lane: private` only for registries you
keep internal.

## 5. Trust model — who signed, and how to check

Signatures are **cosign keyless (OIDC)**: the certificate pins **YOUR
workflow identity** — `https://github.com/your-org/your-repo/.github/workflows/publish.yml@refs/heads/main`
issued by `https://token.actions.githubusercontent.com`. Consumers verify
against the identity they expect, never against DevelApp's.

Verify on a server or in CI (`sbobctl verify`):

```sh
# stage 1 — offline byte-binding: the Rekor bundle must commit sha256 ==
# the shipped entry bytes (no cosign, no network; catches tampered
# entries and signatures that bind different bytes)
node src/cli.ts verify --sbob registry/sbobs/nuget/your-package.linux.yaml

# stage 2 — full keyless verification against a pinned workflow identity
cosign verify-blob \
  --bundle registry/sbobs/nuget/your-package.linux.yaml.bundle \
  --signature registry/sbobs/nuget/your-package.linux.yaml.sig \
  --certificate-identity "https://github.com/your-org/your-repo/.github/workflows/publish.yml@refs/heads/main" \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com \
  registry/sbobs/nuget/your-package.linux.yaml
```

Or as an action (Node ≥ 22, zero npm install):

```yaml
      - uses: DevelApp-ai/Software-Bill-of-Behavior/sbobctl@<sha>
        with:
          command: verify
          sbob: registry/sbobs/nuget/your-package.linux.yaml
          certificate-identity: "https://github.com/your-org/your-repo/.github/workflows/publish.yml@refs/heads/main"
```

## 6. Consume: Monitor → Audit → Enforce

Never jump straight to enforcement. The SBoB standard's rollout ladder:

1. **Monitor** — transpile the profile into detection rules
   (Wazuh audit rules/CDB allowlists, Sigma, Falco, Velociraptor, MDE,
   LogPoint) and watch for drift against the declared behavior.
2. **Audit** — active alerting; behavior outside the profile pages someone.
3. **Enforce** — only after an incident-free audit window: seccomp,
   AppArmor, NetworkPolicy, AppLocker generated from the profile
   (`sbobctl enforce`, gated by a per-workload rollout state file).

Release-to-release, gate deploys with the scored behavior-delta ladder:

```yaml
      - uses: DevelApp-ai/Software-Bill-of-Behavior@<sha>      # sbob-diff-gate
        with:
          old: releases/your-package-1.2.yaml
          new: releases/your-package-1.3.yaml
          fail-on: mandatory
```

## Known constraints (2026-11)

- **Registry PRs need the PAT from step 3** — no `GITHUB_TOKEN` fallback, by design.
- **The parsing substrate restores from a GitHub Packages feed** — forward a
  `read:packages` token (or wait for the nuget.org publication of the
  substrate packages).
- **Registry dialect** is the stability point of the external contract; the
  Kubescape-dialect bridge (`sbobforge convert`) is pinned and
  drift-tested, and a consolidation decision is tracked in
  [SBOB-Forge #29](https://github.com/DevelApp-ai/SBOB-Forge/issues/29).
- **Self-contained binaries** (no .NET install in consumer CI) and
  **Marketplace listings** for both repos are in flight — tracked in
  [SBOB-Registry #24](https://github.com/DevelApp-ai/SBOB-Registry/issues/24).
