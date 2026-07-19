using DevExpress.Data.Filtering;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Actions;
using DevExpress.Persistent.Base;
using Microsoft.Extensions.Logging;
using XafDynamicAssemblies.Module.BusinessObjects;
using XafDynamicAssemblies.Module.Services;

namespace XafDynamicAssemblies.Module.Controllers
{
    /// <summary>
    /// Materializes metadata-defined CustomActions as SimpleActions on every DetailView.
    /// Live: metadata is re-read on each activation -- no compilation, no restart (ACT-001).
    ///
    /// ponytail: XAF Blazor's Ribbon "Edit" container is populated once (Frame/Template creation)
    /// from the Application Model's static ActionToContainerMapping node, itself generated from
    /// constructor-declared actions across all registered Controllers (verified against
    /// FillActionContainersController source: GetActions() resolves container membership by a
    /// static Action.Id list, not by Category, and that list is never rebuilt for the top Ribbon
    /// toolbar after Frame creation). An action created inside OnActivated -- confirmed empirically
    /// via server-side diagnostics showing Active=true and the action present in Controller.Actions,
    /// yet never rendered -- is structurally invisible to the Ribbon no matter its Category or
    /// Active state, because its id was never known at Application Model build time.
    /// Fix: declare a bounded pool of slot actions in the CONSTRUCTOR (stable ids -> get a real
    /// container mapping); OnActivated assigns matching CustomAction rows to slots and hides
    /// unused ones via the Active BoolList (dynamic Active toggling on constructor-declared
    /// actions is standard XAF and works correctly, unlike container membership).
    /// Ceiling: MaxSlots CustomActions per target entity type. Raise MaxSlots, or move to a
    /// SingleChoiceAction dropdown (single constructor-declared action, dynamic Items), if that's
    /// ever a real constraint.
    /// </summary>
    public class MetadataActionDispatcherController : ViewController<DetailView>
    {
        private const int MaxSlots = 10;
        private const string DispatcherActiveKey = "MetadataDispatcher";
        private const string CriteriaFitKey = "CriteriaFit";
        private const string InvalidCriteriaKey = "InvalidCriteria";

        private readonly SimpleAction[] _slots = new SimpleAction[MaxSlots];
        private readonly Dictionary<string, (CustomAction Meta, CriteriaOperator Criteria)> _current =
            new Dictionary<string, (CustomAction, CriteriaOperator)>();

        private IObjectSpace _metadataOs;
        private ILogger<MetadataActionDispatcherController> _logger;

        public MetadataActionDispatcherController()
        {
            for (int i = 0; i < MaxSlots; i++)
            {
                var slot = new SimpleAction(this, $"CustomActionSlot{i}", PredefinedCategory.Edit);
                slot.Active[DispatcherActiveKey] = false;
                slot.Execute += CustomAction_Execute;
                _slots[i] = slot;
            }
        }

        protected override void OnActivated()
        {
            base.OnActivated();
            _logger = Application.ServiceProvider?.GetService(typeof(ILogger<MetadataActionDispatcherController>))
                      as ILogger<MetadataActionDispatcherController>;
            _current.Clear();

            try
            {
                _metadataOs = Application.CreateObjectSpace(typeof(CustomAction));
                var typeName = View.ObjectTypeInfo.Name;
                var rows = _metadataOs.GetObjects<CustomAction>(
                    CriteriaOperator.Parse("IsActive = ? And TargetEntity = ?", true, typeName))
                    // deterministic slot assignment when > MaxSlots match (overflow is logged)
                    .OrderBy(a => a.Caption, StringComparer.Ordinal)
                    .ThenBy(a => a.ID);

                int slotIndex = 0;
                foreach (var row in rows)
                {
                    if (slotIndex >= MaxSlots)
                    {
                        _logger?.LogWarning(
                            "More than {MaxSlots} active CustomActions target '{TypeName}'; extra actions are not shown.",
                            MaxSlots, typeName);
                        break;
                    }

                    var action = _slots[slotIndex++];
                    action.Caption = row.Caption;
                    action.ConfirmationMessage = row.ConfirmationMessage;
                    action.Active[DispatcherActiveKey] = true;
                    action.Enabled.RemoveItem(InvalidCriteriaKey);

                    CriteriaOperator criteria = null;
                    if (!string.IsNullOrWhiteSpace(row.Criteria))
                    {
                        try { criteria = CriteriaOperator.Parse(row.Criteria); }
                        catch (Exception ex)
                        {
                            action.Enabled[InvalidCriteriaKey] = false;
                            _logger?.LogWarning(ex, "CustomAction '{Caption}' has invalid criteria: {Criteria}",
                                row.Caption, row.Criteria);
                        }
                    }
                    _current[action.Id] = (row, criteria);
                }

                // BoolList's empty-list default is "true" (ActionBase.Active is `new BoolList()`,
                // i.e. emptyListValue=true) -- RemoveItem would make an unused slot revert to
                // visible, not hidden. Must explicitly set false.
                for (int i = slotIndex; i < MaxSlots; i++)
                    _slots[i].Active[DispatcherActiveKey] = false;

                if (_current.Count > 0)
                {
                    View.CurrentObjectChanged += View_StateChanged;
                    View.ObjectSpace.ObjectChanged += ObjectSpace_ObjectChanged;
                    UpdateEnabledState();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Metadata action dispatcher failed to activate; no custom actions shown.");
            }
        }

        protected override void OnDeactivated()
        {
            View.ObjectSpace.ObjectChanged -= ObjectSpace_ObjectChanged;
            View.CurrentObjectChanged -= View_StateChanged;

            foreach (var slot in _slots)
            {
                slot.Active[DispatcherActiveKey] = false;
                slot.Enabled.RemoveItem(CriteriaFitKey);
                slot.Enabled.RemoveItem(InvalidCriteriaKey);
            }

            _metadataOs?.Dispose();
            _metadataOs = null;
            base.OnDeactivated();
        }

        private void View_StateChanged(object sender, EventArgs e) => UpdateEnabledState();
        private void ObjectSpace_ObjectChanged(object sender, ObjectChangedEventArgs e) => UpdateEnabledState();

        private void UpdateEnabledState()
        {
            var obj = View.CurrentObject;
            foreach (var kvp in _current)
            {
                var criteria = kvp.Value.Criteria;
                if (ReferenceEquals(criteria, null)) continue;
                var action = Array.Find(_slots, s => s.Id == kvp.Key);
                if (action == null) continue;
                bool fit = false;
                try { fit = obj != null && (View.ObjectSpace.IsObjectFitForCriteria(obj, criteria) ?? false); }
                catch (Exception ex) { _logger?.LogWarning(ex, "Criteria evaluation failed for {Id}", action.Id); }
                action.Enabled[CriteriaFitKey] = fit;
            }
        }

        private void CustomAction_Execute(object sender, SimpleActionExecuteEventArgs e)
        {
            var action = (SimpleAction)sender;
            if (!_current.TryGetValue(action.Id, out var entry))
            {
                ShowError("This action is no longer available. Refresh the view and try again.");
                return;
            }

            var meta = entry.Meta;
            var steps = meta.Steps.OrderBy(s => s.SortOrder).ToList();
            var obj = View.CurrentObject;
            bool anySet = false;
            Type openViewTarget = null;

            foreach (var step in steps)
            {
                switch (step.Kind)
                {
                    case StepKind.SetField:
                    {
                        var member = View.ObjectTypeInfo.FindMember(step.FieldName);
                        if (member == null)
                        {
                            ShowError($"Action '{meta.Caption}': field '{step.FieldName}' not found on {View.ObjectTypeInfo.Name}.");
                            return;
                        }
                        object converted;
                        try { converted = StepValueConverter.Convert(step.Value, member.MemberType); }
                        catch (FormatException ex)
                        {
                            ShowError($"Action '{meta.Caption}': {ex.Message}");
                            return;
                        }
                        member.SetValue(obj, converted);
                        anySet = true;
                        break;
                    }
                    case StepKind.ShowMessage:
                        Application.ShowViewStrategy.ShowMessage(step.MessageText,
                            step.MessageType switch
                            {
                                StepMessageType.Success => InformationType.Success,
                                StepMessageType.Warning => InformationType.Warning,
                                StepMessageType.Error => InformationType.Error,
                                _ => InformationType.Info,
                            });
                        break;
                    case StepKind.OpenView:
                    {
                        var ti = XafTypesInfo.Instance.FindTypeInfo(step.TargetEntityName);
                        if (ti == null)
                        {
                            ShowError($"Action '{meta.Caption}': entity '{step.TargetEntityName}' not found.");
                            return;
                        }
                        openViewTarget = ti.Type;
                        break;
                    }
                }
            }

            if (anySet)
                View.ObjectSpace.CommitChanges();

            if (openViewTarget != null)
            {
                var os = Application.CreateObjectSpace(openViewTarget);
                e.ShowViewParameters.CreatedView = Application.CreateListView(os, openViewTarget, true);
                e.ShowViewParameters.TargetWindow = TargetWindow.Default;
            }
        }

        private void ShowError(string message) =>
            Application.ShowViewStrategy.ShowMessage(message, InformationType.Error);
    }
}
