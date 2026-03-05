# Backburner Ideas

## Runtime Scripted ViewControllers

### Concept
Extend the existing runtime entity system to support scripted ViewControllers — business logic defined and edited at runtime, compiled via Roslyn, loaded via the same AssemblyLoadContext/restart pattern already in place.

### Architecture

```
Monaco Editor (Blazor component)     <- editing experience
  - IntelliSense via Roslyn
  - Syntax highlighting (free)
  - Error squiggles (free)

cs-script / Roslyn compilation       <- already have this
  - Same AssemblyLoadContext pattern
  - Same reference resolution

XAF Controller registration          <- the hard part
  - Process restart (already have this)
  - Or: ScriptableController host
```

### Editor Options
- **BlazorMonaco** — open-source Monaco wrapper, works today on Blazor Server
- **DevExpress Monaco (v26.1, June 2026)** — WinForms only, but same underlying web component
- Monaco is natively a web component, so Blazor Server is the natural fit

### Scripting Engine Options
- **cs-script** — full C# scripting, can emit PDBs for debugger support
- **SharpScript (sharpscript.net)** — more templating-oriented, less suited for controllers
- **Raw Roslyn** — already in use for entity compilation, could extend directly

### What Would Work Well
- Simple actions (button click -> set field, show message, open view)
- Event handlers (ObjectSpace.Committing, View.CurrentObjectChanged)
- Conditional enable/disable based on property values
- Calling existing service methods
- Same graduation pipeline: edit in-app -> compile -> deploy -> graduate to source control

### Hard Parts
- **Debugging** — cs-script can emit PDBs for attached VS debugging; in-browser debugging via debug adapter protocol is a massive undertaking. Pragmatic alternative: inject ILogger, show output in a panel.
- **Reference resolution** — controllers need many more assembly references than entities (Actions, Views, ObjectSpace, etc.)
- **Security** — arbitrary user code runs in-process with full trust
- **Controller lifecycle** — XAF discovers controllers at startup via reflection; hot-loading mid-process won't work (same restart limitation)

### Pragmatic Alternative: Metadata-Driven Actions
Instead of free-form C# controllers, a constrained action builder covering 80% of use cases:

| Field           | Example                          |
|-----------------|----------------------------------|
| ActionName      | ApproveOrder                     |
| ActionType      | SimpleAction                     |
| TargetEntity    | Order                            |
| TargetView      | DetailView                       |
| Criteria        | Status = 'Pending'               |
| On Execute      | SetField: Status = 'Approved'    |
| On Execute      | ShowMessage: "Order approved"    |

### Links
- cs-script: https://github.com/oleg-shilo/cs-script
- SharpScript: https://sharpscript.net/
- BlazorMonaco: https://github.com/nicknow/BlazorMonaco
- DevExpress Monaco roadmap: https://community.devexpress.com/Blogs/winforms/archive/2026/02/24/winforms-june-2026-roadmap-v26-1.aspx
