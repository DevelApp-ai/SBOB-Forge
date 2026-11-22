# open-read

Equivalence: `opens: [(path: /etc/app/config.json, flags: [O_RDONLY])]`, no other sections.

Languages: csharp, java, python, go, js, ts, php, powershell, c, cpp (10).

Excluded: bash — the bash catalog has no read-redirect rule (only redir-truncate/append); `cat <file>` and `< file` are not cataloged opens. Adding a native bash read rule would re-admit bash here.
