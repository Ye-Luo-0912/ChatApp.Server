namespace Core.Settings;

/// <summary>
/// Version of the login-risk decision rules used by the durable analysis
/// worker. The version is copied onto each outbox row so a retry is evaluated
/// with an auditable rule set rather than silently changing meaning after a
/// deployment.
/// </summary>
public sealed class LoginRiskOptions
{
    public const string SectionName = "LoginRisk";

    /// <summary>
    /// Enables durable login-risk signal creation in the API process.  A
    /// Worker still owns the expensive analysis; API-only performance runs
    /// can disable this signal explicitly so the measured request path does
    /// not contain an unrelated database write.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public int RuleVersion { get; set; } = 1;
}
