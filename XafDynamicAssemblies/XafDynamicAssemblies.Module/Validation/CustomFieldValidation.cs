using System.Text.RegularExpressions;

namespace XafDynamicAssemblies.Module.Validation
{
    public static class CustomFieldValidation
    {
        private static readonly Regex ValidIdentifierRegex = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

        private static readonly HashSet<string> ReservedFieldNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Id", "ID", "ObjectType", "GCRecord", "OptimisticLockField"
        };

        public static bool IsValidIdentifier(string name)
        {
            return !string.IsNullOrWhiteSpace(name) && ValidIdentifierRegex.IsMatch(name);
        }

        public static bool IsReservedFieldName(string name)
        {
            return ReservedFieldNames.Contains(name);
        }
    }
}
