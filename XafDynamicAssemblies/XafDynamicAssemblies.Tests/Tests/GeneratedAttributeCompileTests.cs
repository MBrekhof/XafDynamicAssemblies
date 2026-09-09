using XafDynamicAssemblies.Module.BusinessObjects;
using XafDynamicAssemblies.Module.Services;

namespace XafDynamicAssemblies.Tests.Tests;

/// <summary>DATA-004: the UI attribute columns are now read from the DB, so every attribute the generator emits must compile.</summary>
public class GeneratedAttributeCompileTests
{
    [Fact]
    public void All_field_attributes_compile()
    {
        // Force the DX assemblies the generated source references into the AppDomain.
        _ = typeof(DevExpress.Persistent.BaseImpl.EF.BaseObject);
        _ = typeof(DevExpress.Persistent.Base.NavigationItemAttribute);
        _ = typeof(DevExpress.ExpressApp.DC.FieldSizeAttribute);
        _ = typeof(DevExpress.Persistent.Base.ImmediatePostDataAttribute);

        var cc = new CustomClass { ClassName = "AttrProbe", NavigationGroup = "Probe" };
        cc.Fields.Add(new CustomField
        {
            FieldName = "Notes", TypeName = "System.String", StringMaxLength = -1,
            IsVisibleInListView = false, IsVisibleInDetailView = false, IsEditable = false,
            IsImmediatePostData = true, ToolTip = "tip", DisplayName = "Display",
        });
        cc.Fields.Add(new CustomField { FieldName = "Code", TypeName = "System.String", StringMaxLength = 20, IsRequired = true });

        var result = RuntimeAssemblyBuilder.ValidateCompilation(new List<CustomClass> { cc });
        Assert.True(result.Success, string.Join("\n", result.Errors) + "\n" + string.Join("\n", result.GeneratedSources.Values));
    }
}
