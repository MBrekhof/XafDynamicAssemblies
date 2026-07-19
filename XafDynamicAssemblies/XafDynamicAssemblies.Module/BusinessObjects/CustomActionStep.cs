using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using DevExpress.ExpressApp.DC;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;
using DevExpress.Persistent.Validation;

namespace XafDynamicAssemblies.Module.BusinessObjects
{
    public enum StepKind
    {
        SetField,
        ShowMessage,
        OpenView
    }

    public enum StepMessageType
    {
        Info,
        Success,
        Warning,
        Error
    }

    [DefaultProperty(nameof(DisplayText))]
    [XafDisplayName("Action Step")]
    public class CustomActionStep : BaseObject
    {
        [ForeignKey(nameof(CustomAction))]
        public virtual Guid? CustomActionId { get; set; }
        public virtual CustomAction CustomAction { get; set; }

        public virtual int SortOrder { get; set; }

        [ImmediatePostData]
        public virtual StepKind Kind { get; set; } = StepKind.SetField;

        // SetField
        [RuleRequiredField(TargetCriteria = "Kind = 'SetField'")]
        [XafDisplayName("Field Name")]
        public virtual string FieldName { get; set; }

        public virtual string Value { get; set; }

        // ShowMessage
        [RuleRequiredField(TargetCriteria = "Kind = 'ShowMessage'")]
        [XafDisplayName("Message Text")]
        public virtual string MessageText { get; set; }

        public virtual StepMessageType MessageType { get; set; } = StepMessageType.Info;

        // OpenView
        [RuleRequiredField(TargetCriteria = "Kind = 'OpenView'")]
        [XafDisplayName("Target Entity Name")]
        public virtual string TargetEntityName { get; set; }

        [NotMapped]
        [Browsable(false)]
        public string DisplayText => Kind switch
        {
            StepKind.SetField => $"{SortOrder}. Set {FieldName} = {Value}",
            StepKind.ShowMessage => $"{SortOrder}. Message: {MessageText}",
            StepKind.OpenView => $"{SortOrder}. Open {TargetEntityName}",
            _ => SortOrder.ToString()
        };
    }
}
