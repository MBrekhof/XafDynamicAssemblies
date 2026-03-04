using System.Text.RegularExpressions;

namespace XafDynamicAssemblies.Module.Validation
{
    public static class CustomClassValidation
    {
        private static readonly Regex ValidIdentifierRegex = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

        private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch",
            "char", "checked", "class", "const", "continue", "decimal", "default",
            "delegate", "do", "double", "else", "enum", "event", "explicit",
            "extern", "false", "finally", "fixed", "float", "for", "foreach",
            "goto", "if", "implicit", "in", "int", "interface", "internal",
            "is", "lock", "long", "namespace", "new", "null", "object",
            "operator", "out", "override", "params", "private", "protected",
            "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
            "sizeof", "stackalloc", "static", "string", "struct", "switch",
            "this", "throw", "true", "try", "typeof", "uint", "ulong",
            "unchecked", "unsafe", "ushort", "using", "virtual", "void",
            "volatile", "while"
        };

        private static readonly HashSet<string> ReservedTypeNames = new(StringComparer.Ordinal)
        {
            "BaseObject", "CustomClass", "CustomField",
            "Object", "String", "Type", "Assembly",
            "DbContext", "XafDynamicAssembliesEFCoreDbContext"
        };

        public static bool IsValidIdentifier(string name)
        {
            return !string.IsNullOrWhiteSpace(name) && ValidIdentifierRegex.IsMatch(name);
        }

        public static bool IsCSharpKeyword(string name)
        {
            return CSharpKeywords.Contains(name);
        }

        public static bool IsReservedTypeName(string name)
        {
            return ReservedTypeNames.Contains(name);
        }
    }
}
