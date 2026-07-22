namespace Core.Interfaces;

/// <summary>登录风险异步分析（地理/ASN），不阻塞登录热路径。</summary>
public interface ILoginRiskAnalyzer
{
    void Enqueue(LoginRiskWorkItem item);
}

/// <param name="IpChanged">热路径 IP 粗信号（与既有会话不一致）；不单独触发通知。</param>
public sealed record LoginRiskWorkItem(
    long UserId,
    string? ClientIp,
    string? DeviceId,
    bool IsNewDevice,
    string? SessionId,
    bool IpChanged = false);
