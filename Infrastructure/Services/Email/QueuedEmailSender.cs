using Core.Interfaces;
using Core.Models.Email;
using Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Services.Email;

/// <summary>
/// API 侧邮件发送门面：写入持久化 Outbox 后立即返回。
/// </summary>
public sealed class QueuedEmailSender(
    IServiceScopeFactory scopeFactory,
    ITsidGenerator tsidGenerator,
    EmailOutboxMetrics metrics) : IEmailSender
{
    public async Task<EmailResult> SendEmailAsync(
        string to, string subject, string body, bool isHtml = true, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(to)
            || string.IsNullOrWhiteSpace(subject)
            || string.IsNullOrWhiteSpace(body))
        {
            return new EmailResult { IsSuccess = false, ErrorMessage = "收件人、主题和内容不能为空" };
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        var now = DateTime.UtcNow;
        db.EmailOutbox.Add(new EmailOutboxItem
        {
            Id = tsidGenerator.GenerateTsid(),
            To = to.Trim(),
            Subject = subject,
            Body = body,
            IsHtml = isHtml,
            Status = EmailOutboxStatus.Pending,
            AttemptCount = 0,
            CreatedAt = now,
            UpdatedAt = now,
            NextAttemptAt = now,
        });

        await db.SaveChangesAsync(cancellation).ConfigureAwait(false);
        metrics.RecordEnqueued();

        return new EmailResult { IsSuccess = true };
    }

    public Task<EmailResult> SendVerificationEmailAsync(
        string to, string username, string verificationToken, CancellationToken cancellation)
        => SendEmailAsync(to, "verify", verificationToken, true, cancellation);
}
