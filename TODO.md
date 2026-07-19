# TODO — XafDynamicAssemblies

## P3: Low

#### ACT-001: Metadata-driven action builder for runtime entities (ID: 1052)

The pragmatic 80% alternative to free-form scripted ViewControllers (see BACKBURNER.md):
`CustomAction` metadata rows — ActionName, ActionType (SimpleAction), TargetEntity,
TargetView, Criteria (enable/visibility), and OnExecute steps (`SetField`, `ShowMessage`,
later maybe `OpenView`/`CallService`) — rendered as real XAF SimpleActions on runtime entity
views via one generic dispatcher controller. Rides the existing deploy → exit-42 → restart
pipeline; no arbitrary user C# in-process, so none of the security/debugging costs that keep
scripted controllers on the backburner. Graduation story: export as generated controller
source, same as entities.
