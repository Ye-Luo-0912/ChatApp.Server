using Core.Interfaces.Auth;
using Core.Models.Identity;
using Core.Models.Token;

namespace Core.Interfaces;

/// <summary>
/// 令牌服务统一门面（Facade），供业务层（如 AuthService）直接依赖。
/// <para>
/// 组合了四个子职责，也可按需单独注入子接口：
/// <list type="bullet">
///   <item><see cref="ITokenGenerator"/> — 生成密码学安全的随机令牌字符串（纯计算，无 IO）</item>
///   <item><see cref="IAccessTokenStore"/> — 访问令牌在 Redis 中的存储/查询/撤销</item>
///   <item><see cref="IRefreshTokenStore"/> — 刷新令牌在 Redis 中的存储/校验/撤销/轮换</item>
///   <item><see cref="ISessionStore"/> — 会话记录在 Redis 中的查询与撤销</item>
/// </list>
/// </para>
/// </summary>
public interface ITokenService : ITokenGenerator, IAccessTokenStore, IRefreshTokenStore, ISessionStore
{
    /// <summary>
    /// 登录时一次性签发访问令牌和刷新令牌，并将两者持久化到 Redis。
    /// 内部生成 <c>SessionId</c> 并同步写入 <see cref="SessionRecord"/>，同时在 <see cref="RefreshToken"/> 中
    /// 记录当前访问令牌的 Redis 键，供后续轮换和会话撤销使用。
    /// </summary>
    Task<TokenIssueResult> IssueLoginTokensAsync(ApplicationUser user, IList<string> roles, CancellationToken cancellationToken = default);

    /// <summary>
    /// 仅签发并存储一枚访问令牌，适用于外部直接调用场景（如管理员强制刷新）。
    /// <para>
    /// 令牌刷新请使用 <see cref="IssueRefreshTokensAsync"/>，它会同时处理旧访问令牌的撤销。
    /// </para>
    /// </summary>
    Task<string> IssueAccessTokenAsync(ApplicationUser user, IList<string> roles, string? sessionId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 原子地完成令牌轮换：校验并消费旧刷新令牌、撤销旧访问令牌，同时签发并持久化新的访问令牌和刷新令牌。
    /// <para>
    /// 消费与写入在同一 CAS 事务中完成；同一旧刷新令牌并发调用时严格只有一次成功。
    /// 失败（令牌无效、设备不匹配、已被并发消费）时返回 <see langword="null"/>。
    /// </para>
    /// </summary>
    /// <returns>成功时返回新签发的令牌、实际过期时间和轮换后的设备凭据；失败返回 <see langword="null"/>。</returns>
    Task<TokenRotationResult?> IssueRefreshTokensAsync(
        string userId, string oldRefreshToken, ApplicationUser user, IList<string> roles, CancellationToken cancellationToken = default);

    /// <summary>
    /// 在同一安全变更完成后原子轮换当前会话的刷新令牌。旧刷新令牌的
    /// SecurityVersion 可以是变更前版本，但仍必须通过 token CAS、设备绑定
    /// 和 session 关联校验；其它会话由业务层显式撤销。
    /// </summary>
    Task<TokenRotationResult?>
        ReissueSessionAfterSecurityMutationAsync(
            string userId,
            string oldRefreshToken,
            ApplicationUser user,
            IList<string> roles,
            string? expectedSessionId = null,
            CancellationToken cancellationToken = default);
}
