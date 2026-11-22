# dynamic-open-quarantine

Equivalence: the interpolated/identifier open fails closed identically in every language — nothing emitted for the dynamic path — while the sibling literal open lifts normally: `opens: [(path: /var/log/app.log, flags: [O_WRONLY, O_CREAT, O_APPEND])]`. Every language quarantines the dynamic candidate (tier Q, primitive Open, attributed); coverage report records it.

The sibling literal open is required: an all-quarantine profile is a section-shadowed profile and the emitter fail-closes it (SBOB-Forge #49), so a dynamic-only scenario cannot emit at all.

Languages: all 11 (csharp, java, python, go, js, ts, php, powershell, bash, c, cpp).
