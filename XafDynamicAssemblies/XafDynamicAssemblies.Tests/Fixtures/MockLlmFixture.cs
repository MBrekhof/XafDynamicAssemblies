namespace XafDynamicAssemblies.Tests.Fixtures;

public class MockLlmFixture : IAsyncLifetime
{
    private MockLlm.MockLlmServer _server = null!;

    public async Task InitializeAsync()
    {
        _server = new MockLlm.MockLlmServer(TestSettings.MockLlmPort);
        await _server.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync();
    }
}
