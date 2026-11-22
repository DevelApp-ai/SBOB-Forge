# listen-ingress

Equivalence: `ingress: [(port: 8080, protocol: TCP)]` (identifier `port-8080-ingress`), no other sections.

Languages: csharp, java, python, go, js, ts, c, cpp (8). C / C++ use the sockaddr `sin_port` hoisting (documented M5 decision).

Excluded: bash, php, powershell — no ingress catalog rule exists (bash `nc -l` is egress-shaped, PowerShell TCP-listener idioms are not cataloged, php has no listen primitive rule).
