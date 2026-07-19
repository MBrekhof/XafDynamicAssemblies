# TODO — XafDynamicAssemblies

## P2: Medium

#### SEC-002: AngleSharp 0.17.1 advisory via HtmlSanitizer (NU1902) (ID: 1054)

.NET 10 restore audits transitive packages and flags AngleSharp 0.17.1 (moderate,
GHSA-pgww-w46g-26qg), pulled in by HtmlSanitizer 9.0.892 — the latest stable, pinned to
AngleSharp 0.17.x; no patched stable exists upstream. Pre-existing exposure, newly visible
with the .NET 10 upgrade. Usage: sanitizing AI-chat markdown/HTML output. Action: watch for
an HtmlSanitizer release built on AngleSharp 1.x and bump; do not suppress the warning.

(Completed work: see `docs/DONE.md`. Future ideas: `BACKBURNER.md` — including the ACT-001
fast-follows: AI-chat action verbs, ListView targets, expression values.)
