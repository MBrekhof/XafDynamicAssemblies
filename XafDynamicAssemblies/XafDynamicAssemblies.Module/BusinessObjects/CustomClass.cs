using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DC;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;
using DevExpress.Persistent.Validation;
using XafDynamicAssemblies.Module.Validation;

namespace XafDynamicAssemblies.Module.BusinessObjects
{
    public enum CustomClassStatus
    {
        Runtime,
        Graduating,
        Compiled
    }

    [DefaultClassOptions]
    [NavigationItem("Schema Management")]
    [DefaultProperty(nameof(ClassName))]
    public class CustomClass : BaseObject
    {
        public virtual string ClassName { get; set; }
        public virtual string NavigationGroup { get; set; }
        public virtual string Description { get; set; }
        public virtual CustomClassStatus Status { get; set; } = CustomClassStatus.Runtime;

        [Aggregated]
        public virtual IList<CustomField> Fields { get; set; } = new ObservableCollection<CustomField>();

        /// <summary>
        /// Stores the generated C# source code after graduation.
        /// </summary>
        [VisibleInListView(false)]
        public virtual string GraduatedSource { get; set; }

        [RuleFromBoolProperty("CustomClass_ValidClassName", DefaultContexts.Save,
            "Class Name must be a valid C# identifier (letters, digits, underscores; cannot start with a digit).")]
        [NotMapped]
        [Browsable(false)]
        public bool IsClassNameValid => !string.IsNullOrWhiteSpace(ClassName) && CustomClassValidation.IsValidIdentifier(ClassName);

        [RuleFromBoolProperty("CustomClass_NotKeyword", DefaultContexts.Save,
            "Class Name cannot be a C# keyword.")]
        [NotMapped]
        [Browsable(false)]
        public bool IsClassNameNotKeyword => string.IsNullOrWhiteSpace(ClassName) || !CustomClassValidation.IsCSharpKeyword(ClassName);

        [RuleFromBoolProperty("CustomClass_NotReservedType", DefaultContexts.Save,
            "Class Name conflicts with a built-in type name.")]
        [NotMapped]
        [Browsable(false)]
        public bool IsClassNameNotReserved => string.IsNullOrWhiteSpace(ClassName) || !CustomClassValidation.IsReservedTypeName(ClassName);
    }
}
