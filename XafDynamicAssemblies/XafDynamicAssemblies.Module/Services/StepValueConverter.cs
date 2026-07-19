using System.Globalization;

namespace XafDynamicAssemblies.Module.Services
{
    /// <summary>Converts CustomActionStep string literals to member types (invariant culture).</summary>
    public static class StepValueConverter
    {
        public static object Convert(string raw, Type targetType)
        {
            var underlying = Nullable.GetUnderlyingType(targetType);
            var effective = underlying ?? targetType;

            if (string.IsNullOrEmpty(raw))
            {
                if (underlying != null || !effective.IsValueType)
                    return effective == typeof(string) ? raw : null;
                throw new FormatException($"Empty value cannot be converted to non-nullable {effective.Name}.");
            }

            try
            {
                if (effective == typeof(string)) return raw;
                if (effective == typeof(Guid)) return Guid.Parse(raw);
                if (effective == typeof(bool)) return bool.Parse(raw);
                if (effective == typeof(DateTime))
                    return DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None);
                if (effective.IsEnum) return Enum.Parse(effective, raw, ignoreCase: true);
                return System.Convert.ChangeType(raw, effective, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is not FormatException)
            {
                throw new FormatException($"Value '{raw}' cannot be converted to {effective.Name}: {ex.Message}");
            }
            catch (FormatException)
            {
                throw new FormatException($"Value '{raw}' cannot be converted to {effective.Name}.");
            }
        }
    }
}
