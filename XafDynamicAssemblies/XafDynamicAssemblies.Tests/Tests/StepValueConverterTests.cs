using XafDynamicAssemblies.Module.Services;

namespace XafDynamicAssemblies.Tests.Tests;

public class StepValueConverterTests
{
    [Theory]
    [InlineData("hello", typeof(string), "hello")]
    [InlineData("42", typeof(int), 42)]
    [InlineData("true", typeof(bool), true)]
    [InlineData("3.5", typeof(double), 3.5)]
    public void Converts_primitives(string raw, Type target, object expected)
        => Assert.Equal(expected, StepValueConverter.Convert(raw, target));

    [Fact]
    public void Converts_decimal_invariant()
        => Assert.Equal(12.34m, StepValueConverter.Convert("12.34", typeof(decimal)));

    [Fact]
    public void Empty_string_to_nullable_is_null()
        => Assert.Null(StepValueConverter.Convert("", typeof(int?)));

    [Fact]
    public void Empty_string_to_string_stays_empty()
        => Assert.Equal("", StepValueConverter.Convert("", typeof(string)));

    [Fact]
    public void Empty_string_to_nonnullable_throws()
        => Assert.Throws<FormatException>(() => StepValueConverter.Convert("", typeof(int)));

    [Fact]
    public void Garbage_to_int_throws_with_message()
    {
        var ex = Assert.Throws<FormatException>(() => StepValueConverter.Convert("abc", typeof(int)));
        Assert.Contains("abc", ex.Message);
    }

    [Fact]
    public void Converts_guid_and_datetime()
    {
        var g = Guid.NewGuid();
        Assert.Equal(g, StepValueConverter.Convert(g.ToString(), typeof(Guid)));
        Assert.Equal(new DateTime(2026, 7, 19), StepValueConverter.Convert("2026-07-19", typeof(DateTime)));
    }
}
