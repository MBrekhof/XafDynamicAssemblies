using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DC;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;
using DevExpress.Persistent.Validation;
using XafDynamicAssemblies.Module.Services;
using XafDynamicAssemblies.Module.Validation;

namespace XafDynamicAssemblies.Module.BusinessObjects
{
    [DefaultClassOptions]
    [NavigationItem("Schema Management")]
    [DefaultProperty(nameof(FieldName))]
    public class CustomField : BaseObject
    {
        [ForeignKey(nameof(CustomClass))]
        public virtual Guid? CustomClassId { get; set; }
        public virtual CustomClass CustomClass { get; set; }
        public virtual string FieldName { get; set; }
        public virtual string TypeName { get; set; } = "System.String";
        public virtual bool IsRequired { get; set; }
        public virtual bool IsDefaultField { get; set; }
        public virtual string Description { get; set; }
        public virtual string ReferencedClassName { get; set; }
        public virtual int SortOrder { get; set; }

        // XAF property attributes
        public virtual bool IsImmediatePostData { get; set; }
        public virtual int? StringMaxLength { get; set; }
        public virtual bool IsVisibleInListView { get; set; } = true;
        public virtual bool IsVisibleInDetailView { get; set; } = true;
        public virtual bool IsEditable { get; set; } = true;
        public virtual string ToolTip { get; set; }
        public virtual string DisplayName { get; set; }

        [RuleFromBoolProperty("CustomField_ValidFieldName", DefaultContexts.Save,
            "Field Name must be a valid C# identifier (letters, digits, underscores; cannot start with a digit).")]
        [NotMapped]
        [Browsable(false)]
        public bool IsFieldNameValid => !string.IsNullOrWhiteSpace(FieldName) && CustomFieldValidation.IsValidIdentifier(FieldName);

        [RuleFromBoolProperty("CustomField_NotReservedField", DefaultContexts.Save,
            "Field Name is reserved (Id, ObjectType, GCRecord, OptimisticLockField).")]
        [NotMapped]
        [Browsable(false)]
        public bool IsFieldNameNotReserved => string.IsNullOrWhiteSpace(FieldName) || !CustomFieldValidation.IsReservedFieldName(FieldName);

        [RuleFromBoolProperty("CustomField_ValidTypeName", DefaultContexts.Save,
            "Type Name must be a supported CLR type (or 'Reference' with a Referenced Class Name).")]
        [NotMapped]
        [Browsable(false)]
        public bool IsTypeNameValid => string.IsNullOrWhiteSpace(TypeName) || SupportedTypes.IsSupported(TypeName);

        [RuleFromBoolProperty("CustomField_ReferenceRequiresClass", DefaultContexts.Save,
            "A Reference field requires a Referenced Class Name.")]
        [NotMapped]
        [Browsable(false)]
        public bool IsReferenceClassValid => TypeName != "Reference" || !string.IsNullOrWhiteSpace(ReferencedClassName);
    }
}
