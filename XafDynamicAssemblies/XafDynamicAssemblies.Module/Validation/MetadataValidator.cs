using XafDynamicAssemblies.Module.BusinessObjects;
using XafDynamicAssemblies.Module.Services;

namespace XafDynamicAssemblies.Module.Validation
{
    /// <summary>
    /// The one validation every write path and every compile path converges on (SEC-003, AI-001).
    /// The XAF RuleFromBoolProperty rules on CustomClass/CustomField only fire in the UI save
    /// path; the AI tools write through a non-secured ObjectSpace and the generator reads rows
    /// straight from SQL, so this is the guard that keeps arbitrary strings out of generated C#.
    /// </summary>
    public static class MetadataValidator
    {
        /// <summary>Returns the first problem with the class metadata, or null when it is safe to emit.</summary>
        public static string Validate(CustomClass cc)
        {
            var className = cc.ClassName;
            if (!CustomClassValidation.IsValidIdentifier(className))
                return "Class Name must be a valid C# identifier (letters, digits, underscores; cannot start with a digit).";
            if (CustomClassValidation.IsCSharpKeyword(className))
                return $"Class Name '{className}' is a C# keyword.";
            if (CustomClassValidation.IsReservedTypeName(className))
                return $"Class Name '{className}' conflicts with a built-in type name.";

            var seen = new HashSet<string>(StringComparer.Ordinal);
            // Distinct(): EF relationship fixup plus an explicit Fields.Add can hold the same
            // tracked instance twice; only distinct instances with the same name are duplicates.
            var fields = (cc.Fields ?? new List<CustomField>()).Distinct();
            foreach (var field in fields)
            {
                var name = field.FieldName;
                if (!CustomFieldValidation.IsValidIdentifier(name))
                    return $"Field Name '{name}' must be a valid C# identifier (letters, digits, underscores; cannot start with a digit).";
                if (CustomClassValidation.IsCSharpKeyword(name))
                    return $"Field Name '{name}' is a C# keyword.";
                if (CustomFieldValidation.IsReservedFieldName(name))
                    return $"Field Name '{name}' is reserved (Id, ObjectType, GCRecord, OptimisticLockField).";
                if (name == className)
                    return $"Field Name '{name}' cannot be the same as its class name.";
                if (!seen.Add(name))
                    return $"Field Name '{name}' is declared more than once.";

                var isReference = !string.IsNullOrWhiteSpace(field.ReferencedClassName)
                    && (field.TypeName == "Reference" || string.IsNullOrWhiteSpace(field.TypeName));
                if (isReference)
                {
                    var refName = field.ReferencedClassName;
                    if (!CustomClassValidation.IsValidIdentifier(refName) || CustomClassValidation.IsCSharpKeyword(refName))
                        return $"Referenced Class Name '{refName}' on field '{name}' must be a valid C# identifier.";
                    // The generator emits an FK companion property "<FieldName>Id".
                    if (!seen.Add(name + "Id"))
                        return $"Field '{name}' generates '{name}Id', which collides with another field.";
                }
                else
                {
                    if (field.TypeName == "Reference")
                        return $"Field '{name}' is a Reference but has no Referenced Class Name.";
                    if (string.IsNullOrWhiteSpace(field.TypeName) || !SupportedTypes.IsSupported(field.TypeName))
                        return $"Field '{name}' has unsupported type '{field.TypeName}'. Supported: {string.Join(", ", SupportedTypes.AllTypeNames)}.";
                }
            }

            return null;
        }

        /// <summary>Validates every class; yields "ClassName: message" for each failure.</summary>
        public static List<string> ValidateAll(IEnumerable<CustomClass> classes)
        {
            var errors = new List<string>();
            foreach (var cc in classes)
            {
                var error = Validate(cc);
                if (error != null)
                    errors.Add($"{cc.ClassName}: {error}");
            }
            return errors;
        }
    }
}
