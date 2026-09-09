using XafDynamicAssemblies.Module.BusinessObjects;
using XafDynamicAssemblies.Module.Validation;

namespace XafDynamicAssemblies.Tests.Tests;

/// <summary>DATA-007: pure mismatch function of the startup guard.</summary>
public class SchemaGuardTests
{
    private static CustomField Plain(string type) => new() { FieldName = "F", TypeName = type };
    private static CustomField Ref(string target) => new() { FieldName = "Boss", TypeName = "Reference", ReferencedClassName = target };
    private static readonly string[] NoFk = Array.Empty<string>();
    private static bool Throw() => throw new InvalidOperationException("probe must not run");

    [Theory]
    [InlineData("System.String", "text")]
    [InlineData("System.String", "character varying")]
    [InlineData("System.Int32", "integer")]
    [InlineData("System.DateTime", "timestamp with time zone")]
    [InlineData("System.Decimal", "numeric")]
    [InlineData("System.Byte[]", "bytea")]
    public void Compatible_types_pass(string typeName, string dataType)
        => Assert.Null(SchemaGuard.FindMismatch(Plain(typeName), dataType, NoFk, true, Throw));

    [Fact]
    public void Absent_column_passes()
        => Assert.Null(SchemaGuard.FindMismatch(Plain("System.Int32"), null, NoFk, true, Throw));

    [Fact]
    public void Unknown_types_on_either_side_pass()
    {
        Assert.Null(SchemaGuard.FindMismatch(Plain("Some.Unknown"), "integer", NoFk, true, Throw));
        // Npgsql can read date -> DateTime and money -> decimal; outside the vocabulary means "do not judge"
        Assert.Null(SchemaGuard.FindMismatch(Plain("System.DateTime"), "date", NoFk, true, Throw));
        Assert.Null(SchemaGuard.FindMismatch(Plain("System.Decimal"), "money", NoFk, true, Throw));
    }

    [Fact]
    public void Type_mismatch_is_reported()
        => Assert.Contains("column is text", SchemaGuard.FindMismatch(Plain("System.Int32"), "text", NoFk, true, Throw));

    [Fact]
    public void Reference_on_non_uuid_column_is_reported()
        => Assert.NotNull(SchemaGuard.FindMismatch(Ref("Company"), "text", NoFk, true, Throw));

    [Fact]
    public void Reference_with_matching_fk_passes()
        => Assert.Null(SchemaGuard.FindMismatch(Ref("Company"), "uuid", new[] { "Company" }, true, Throw));

    [Fact]
    public void Fk_retarget_is_reported_for_runtime_targets_only()
    {
        Assert.Contains("targets Department", SchemaGuard.FindMismatch(Ref("Company"), "uuid", new[] { "Department" }, true, Throw));
        // Compiled target: the FK legitimately points at the mapped table (CustomClass -> CustomClasses)
        Assert.Null(SchemaGuard.FindMismatch(Ref("CustomClass"), "uuid", new[] { "CustomClasses" }, false, Throw));
    }

    [Fact]
    public void Reference_without_fk_and_dangling_ids_is_reported()
        => Assert.Contains("ADD CONSTRAINT would fail", SchemaGuard.FindMismatch(Ref("Company"), "uuid", NoFk, true, () => true));

    [Fact]
    public void Reference_without_fk_and_valid_ids_passes()
        => Assert.Null(SchemaGuard.FindMismatch(Ref("Company"), "uuid", NoFk, true, () => false));

    [Fact]
    public void Column_name_adds_Id_for_references()
    {
        Assert.Equal("BossId", SchemaGuard.ColumnName(Ref("Company")));
        Assert.Equal("F", SchemaGuard.ColumnName(Plain("System.String")));
    }
}
