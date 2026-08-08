using Core.Models.Email;

namespace Core.Interfaces;

/// <summary>邮件 Outbox 管理查询与安全重试的应用边界。</summary>
public interface IEmailOutboxAdminService
{
    Task<IReadOnlyList<EmailOutboxAdminItemDto>> ListDeadAsync(
        int limit = 50,
        CancellationToken cancellationToken = default);

    Task<bool> RetryAsync(
        long id,
        CancellationToken cancellationToken = default);
}
