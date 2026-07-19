# TODO — XafDynamicAssemblies

## P2: Medium

#### SEC-002: AngleSharp 0.17.1 advisory via HtmlSanitizer (NU1902) (ID: 1054)

.NET 10 restore audits transitive packages and flags AngleSharp 0.17.1 (moderate,
GHSA-pgww-w46g-26qg / CVE-2026-54570), pulled in by HtmlSanitizer 9.0.892 which pins
AngleSharp to exactly [0.17.1] — a direct override is impossible. **Verified 2026-07-19:**
the fix shipped in stable AngleSharp 1.5.0 (1.5.2 current); the blocker is solely
HtmlSanitizer's pin. **HtmlSanitizer 9.1.949-beta already depends on AngleSharp 1.5.1
(patched)** — adopt the 9.1.x line the moment it goes stable, or take the beta earlier if
acceptable (it also pulls AngleSharp.Css 1.0.0-beta.216). Relevance is real, not just audit
noise: the advisory describes mXSS payloads that bypass sanitizers relying on AngleSharp
parsing — exactly our AI-chat HTML sanitization path. Do not suppress the warning.

(Completed work: see `docs/DONE.md`. Future ideas: `BACKBURNER.md` — including the ACT-001
fast-follows: AI-chat action verbs, ListView targets, expression values.)
