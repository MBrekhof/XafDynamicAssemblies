using XafDynamicAssemblies.Module.BusinessObjects;
using XafDynamicAssemblies.Module.Services;
using XafDynamicAssemblies.Module.Validation;

namespace XafDynamicAssemblies.Tests.Tests;

/// <summary>SEC-003 / AI-001: metadata strings must never reach generated C# unvalidated.</summary>
public class MetadataValidatorTests
{
    private static CustomClass Class(string name, params CustomField[] fields)
    {
        var cc = new CustomClass { ClassName = name, Fields = new List<CustomField>() };
        foreach (var f in fields) cc.Fields.Add(f);
        return cc;
    }

    private static CustomField Field(string name, string type = "System.String", string? referenced = null)
        => new() { FieldName = name, TypeName = referenced == null ? type : "Reference", ReferencedClassName = referenced };

    [Fact]
    public void Valid_class_passes()
        => Assert.Null(MetadataValidator.Validate(Class("Employee", Field("Name"), Field("Age", "System.Int32"), Field("Company", referenced: "Company"))));

    [Theory]
    [InlineData("class")]
    [InlineData("1Bad")]
    [InlineData("Bad Name")]
    [InlineData("Ok\n")]   // trailing newline bypassed the old '$' regex anchor
    [InlineData("")]
    public void Bad_class_name_rejected(string name)
        => Assert.NotNull(MetadataValidator.Validate(Class(name, Field("Name"))));

    [Fact]
    public void Null_class_name_rejected()
        => Assert.NotNull(MetadataValidator.Validate(Class(null!, Field("Name"))));

    [Theory]
    [InlineData("public")]
    [InlineData("Id")]
    [InlineData("Bad-Name")]
    [InlineData("")]
    public void Bad_field_name_rejected(string name)
        => Assert.NotNull(MetadataValidator.Validate(Class("Employee", Field(name))));

    [Fact]
    public void Injection_via_ReferencedClassName_rejected()
    {
        var payload = "Company X {get;set;} static Emp(){System.IO.File.Delete(\"x\");} public virtual Company";
        Assert.NotNull(MetadataValidator.Validate(Class("Employee", Field("Boss", referenced: payload))));
    }

    [Fact]
    public void Unsupported_TypeName_rejected()
        => Assert.NotNull(MetadataValidator.Validate(Class("Employee", Field("X", "System.Object Y {get;set;} public string"))));

    [Fact]
    public void Reference_without_class_rejected()
        => Assert.NotNull(MetadataValidator.Validate(Class("Employee", new CustomField { FieldName = "Boss", TypeName = "Reference" })));

    [Fact]
    public void Duplicate_field_rejected()
        => Assert.NotNull(MetadataValidator.Validate(Class("Employee", Field("Name"), Field("Name"))));

    [Fact]
    public void Field_named_like_class_rejected()
        => Assert.NotNull(MetadataValidator.Validate(Class("Employee", Field("Employee"))));

    [Fact]
    public void Reference_fk_companion_collision_rejected()
        => Assert.NotNull(MetadataValidator.Validate(Class("Order", Field("CustomerId", "System.Guid"), Field("Customer", referenced: "Customer"))));

    [Fact]
    public void ValidateCompilation_reports_bad_metadata_as_errors_not_throw()
    {
        var result = RuntimeAssemblyBuilder.ValidateCompilation(new List<CustomClass> { Class("class", Field("Name")) });
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.StartsWith("class:"));
        Assert.Empty(result.GeneratedSources);
    }

    [Fact]
    public void Compile_reports_bad_metadata_as_errors_not_throw()
    {
        var result = RuntimeAssemblyBuilder.Compile(new List<CustomClass> { Class("Ok", Field("Bad Name")) });
        Assert.False(result.Success);
        Assert.Null(result.Assembly);
    }

    [Fact]
    public void GenerateSource_throws_on_invalid_metadata()
        => Assert.Throws<InvalidOperationException>(() => RuntimeAssemblyBuilder.GenerateSource(Class("class", Field("Name"))));

    [Fact]
    public void String_metadata_is_emitted_as_escaped_literal()
    {
        var cc = Class("Employee", Field("Name"));
        cc.NavigationGroup = "HR\")]\npublic class Evil { static Evil() { } }\n//";
        cc.Fields[0].DisplayName = "Line1\r\nLine2 \"quoted\"";
        var source = RuntimeAssemblyBuilder.GenerateSource(cc);
        Assert.Contains("[NavigationItem(\"HR\\\")]\\npublic class Evil", source);
        Assert.Contains("[DisplayName(\"Line1\\r\\nLine2 \\\"quoted\\\"\")]", source);
        Assert.DoesNotContain("\npublic class Evil", source);
    }

    [Fact]
    public void Graduation_description_newline_cannot_escape_comment()
    {
        var cc = Class("Employee", Field("Name"));
        cc.Fields[0].Description = "ok\npublic int Injected { get; set; }";
        var source = GraduationService.GenerateGraduationSource(cc);
        Assert.DoesNotContain("\npublic int Injected", source);
        Assert.Contains("/// <summary>ok public int Injected { get; set; }</summary>", source);
    }
}
