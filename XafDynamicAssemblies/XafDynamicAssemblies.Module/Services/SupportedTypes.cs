namespace XafDynamicAssemblies.Module.Services
{
    public static class SupportedTypes
    {
        private static readonly Dictionary<string, string> ClrToPostgres = new()
        {
            ["System.String"] = "text",
            ["System.Int32"] = "integer",
            ["System.Int64"] = "bigint",
            ["System.Decimal"] = "numeric(18,6)",
            ["System.Double"] = "double precision",
            ["System.Single"] = "real",
            ["System.Boolean"] = "boolean",
            ["System.DateTime"] = "timestamp without time zone",
            ["System.Guid"] = "uuid",
            ["System.Byte[]"] = "bytea",
            ["Reference"] = "uuid",
        };

        public static IReadOnlyList<string> AllTypeNames => ClrToPostgres.Keys.ToList();

        public static string GetPostgresType(string clrTypeName)
        {
            if (ClrToPostgres.TryGetValue(clrTypeName, out var pgType))
                return pgType;

            throw new ArgumentException($"Unsupported CLR type: {clrTypeName}");
        }

        public static bool IsSupported(string clrTypeName)
        {
            return ClrToPostgres.ContainsKey(clrTypeName);
        }

        public static string GetPostgresDefault(string clrTypeName)
        {
            return clrTypeName switch
            {
                "System.String" => "''",
                "System.Int32" or "System.Int64" or "System.Single" or "System.Double" => "0",
                "System.Decimal" => "0",
                "System.Boolean" => "false",
                "System.DateTime" => "CURRENT_TIMESTAMP",
                "System.Guid" => "gen_random_uuid()",
                _ => "NULL"
            };
        }
    }
}
