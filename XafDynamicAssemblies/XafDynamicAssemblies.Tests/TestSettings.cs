using System;

namespace XafDynamicAssemblies.Tests;

public static class TestSettings
{
    public static string BaseUrl =>
        Environment.GetEnvironmentVariable("BASE_URL") ?? "https://localhost:5001";

    public static bool Headless =>
        bool.TryParse(Environment.GetEnvironmentVariable("HEADLESS"), out var h) ? h : true;

    public static int SlowMo =>
        int.TryParse(Environment.GetEnvironmentVariable("SLOW_MO"), out var s) ? s : 0;

    public static int MockLlmPort =>
        int.TryParse(Environment.GetEnvironmentVariable("MOCK_LLM_PORT"), out var p) ? p : 5555;

    public static string DbHost =>
        Environment.GetEnvironmentVariable("DB_HOST") ?? "localhost";

    public static int DbPort =>
        int.TryParse(Environment.GetEnvironmentVariable("DB_PORT"), out var p) ? p : 5434;

    public static string DbName =>
        Environment.GetEnvironmentVariable("DB_NAME") ?? "XafDynamicAssemblies";

    public static string DbUser =>
        Environment.GetEnvironmentVariable("DB_USER") ?? "xafdynamic";

    public static string DbPassword =>
        Environment.GetEnvironmentVariable("DB_PASS") ?? "xafdynamic";

    public static string? AiTestApiKey =>
        Environment.GetEnvironmentVariable("AI_TEST_API_KEY");
}
