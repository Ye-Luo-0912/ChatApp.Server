using Core.Interfaces;

namespace ChatApp.Server.IntegrationTests.Support;

internal sealed class NoopLoginRiskAnalyzer : ILoginRiskAnalyzer
{
    public static NoopLoginRiskAnalyzer Instance { get; } = new();

    public void Enqueue(LoginRiskWorkItem item)
    {
    }
}
