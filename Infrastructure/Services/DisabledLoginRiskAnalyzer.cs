using Core.Interfaces;

namespace Infrastructure.Services;

/// <summary>
/// Explicit no-op used by API-only performance processes.  It is preferable
/// to a bounded in-memory queue: disabled analysis must not enqueue a durable
/// row, perform an external GeoIP call, or silently add database work to the
/// measured login path.
/// </summary>
public sealed class DisabledLoginRiskAnalyzer : ILoginRiskAnalyzer
{
    public static DisabledLoginRiskAnalyzer Instance { get; } = new();

    private DisabledLoginRiskAnalyzer()
    {
    }

    public void Enqueue(LoginRiskWorkItem item)
    {
        // Deliberately empty.  The option is an explicit benchmark/deployment
        // decision, not a best-effort overflow path.
    }
}
