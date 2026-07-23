namespace Core.Models.Auth;

/// <summary>高风险 step-up 用途绑定常量。</summary>
public static class StepUpPurposes
{
    public const string TrustedDevice = "trusted-device";
    public const string DataExport = "data-export";

    public static bool IsKnown(string? purpose)
        => string.Equals(purpose, TrustedDevice, StringComparison.Ordinal)
           || string.Equals(purpose, DataExport, StringComparison.Ordinal);
}
