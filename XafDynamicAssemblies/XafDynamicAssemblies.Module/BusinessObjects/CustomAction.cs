using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using DevExpress.ExpressApp.DC;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;
using DevExpress.Persistent.Validation;

namespace XafDynamicAssemblies.Module.BusinessObjects
{
    [DefaultClassOptions]
    [NavigationItem("Schema Management")]
    [DefaultProperty(nameof(Caption))]
    [XafDisplayName("Custom Action")]
    [RuleCombinationOfPropertiesIsUnique("CustomAction_Caption_Target_Unique", DefaultContexts.Save,
        nameof(Caption) + ";" + nameof(TargetEntity))]
    public class CustomAction : BaseObject
    {
        [RuleRequiredField]
        public virtual string Caption { get; set; } = string.Empty;

        [RuleRequiredField]
        [XafDisplayName("Target Entity")]
        public virtual string TargetEntity { get; set; } = string.Empty;

        [FieldSize(FieldSizeAttribute.Unlimited)]
        public virtual string Criteria { get; set; }

        [XafDisplayName("Confirmation Message")]
        public virtual string ConfirmationMessage { get; set; }

        public virtual bool IsActive { get; set; } = true;

        [Aggregated]
        public virtual IList<CustomActionStep> Steps { get; set; } = new ObservableCollection<CustomActionStep>();

        // ponytail: validated in code, not a rule class — one OpenView per action keeps
        // execute semantics unambiguous (OpenView is always effectively last)
        [RuleFromBoolProperty("CustomAction_SingleOpenView", DefaultContexts.Save,
            "An action may contain at most one OpenView step",
            UsedProperties = nameof(Steps))]
        [NotMapped]
        [Browsable(false)]
        public bool HasAtMostOneOpenView => Steps.Count(s => s.Kind == StepKind.OpenView) <= 1;

        // Criteria parseability is a WARNING on save (target type may not exist yet).
        // Verified via dxdocs: RuleFromBoolPropertyAttribute inherits RuleBaseAttribute.ResultType
        // (ValidationResultType: Error/Warning/Information).
        [RuleFromBoolProperty("CustomAction_CriteriaParseable", DefaultContexts.Save,
            "Criteria could not be parsed — the action will be disabled until fixed",
            UsedProperties = nameof(Criteria), ResultType = ValidationResultType.Warning)]
        [NotMapped]
        [Browsable(false)]
        public bool CriteriaIsParseable
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Criteria)) return true;
                try { DevExpress.Data.Filtering.CriteriaOperator.Parse(Criteria); return true; }
                catch { return false; }
            }
        }
    }
}
