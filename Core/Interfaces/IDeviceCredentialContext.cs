namespace Core.Interfaces;

/// <summary>
/// 当前请求的服务端设备凭据上下文。
/// <para>
/// 设备标识（InstallationId/DeviceId）只是会话分区标识；凭据才是刷新令牌的
/// 服务端签发认证因子。明文凭据不持久化，持久化层只保存摘要。
/// </para>
/// </summary>
public interface IDeviceCredentialContext
{
    /// <summary>读取请求中携带的设备凭据摘要；格式无效或未携带时返回 null。</summary>
    string? GetPresentedDeviceCredentialHash();

    /// <summary>生成一枚新的设备凭据明文，仅供登录/轮换响应返回。</summary>
    string IssueDeviceCredential();
}
