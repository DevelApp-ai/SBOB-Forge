# exec-args

Equivalence: `execs: [(path: /usr/bin/git, args: [/usr/bin/git, status])]` via `execPathPolicy: git -> /usr/bin/git`, no other sections.

Languages: csharp, java, python, go, js, ts, php, powershell, bash, c (10).

Excluded: cpp — the C++ catalog exec rules (`std::system`, `popen`) are shell-mediated and emit `/bin/sh -c git status`, a different (honest) exec section by design. A native `execv*`/`posix_spawn` rule would re-admit cpp here.
