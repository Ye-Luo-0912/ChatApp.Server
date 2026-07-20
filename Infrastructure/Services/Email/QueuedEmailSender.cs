using Core.Interfaces;
using Core.Models.Email;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
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
    public Task<EmailResult> SendEmailAsync(
        string to, string subject, string body, bool isHtml = true, CancellationToken cancellation = default)
        => EnqueueEmailAsync(to, subject, body, isHtml, emailType: null, idempotencyKey: null, cancellation);

    public async Task<EmailResult> EnqueueEmailAsync(
        string to,
        string subject,
        string body,
        bool isHtml = true,
        string? emailType = null,
        string? idempotencyKey = null,
        CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(to)
            || string.IsNullOrWhiteSpace(subject)
            || string.IsNullOrWhiteSpace(body))
        {
            return new EmailResult { IsSuccess = false, ErrorMessage = "收件人、主题和内容不能为空" };
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var exists = await db.EmailOutbox.AsNoTracking().AnyAsync(
                x => x.IdempotencyKey == idempotencyKey
                     && (x.Status == EmailOutboxStatus.Pending
                         || x.Status == EmailOutboxStatus.Processing
                         || x.Status == EmailOutboxStatus.Failed),
                cancellation).ConfigureAwait(false);

            if (exists)
                return new EmailResult { IsSuccess = true };
        }

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
            EmailType = emailType,
            IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey,
        });

        try
        {
            await db.SaveChangesAsync(cancellation).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (PostgresDbException.IsUniqueViolation(
                  ex, PostgresDbException.EmailOutboxIdempotencyConstraint))
        {
            // 仅幂等唯一约束冲突视为已入队
            return new EmailResult { IsSuccess = true };
        }

        metrics.RecordEnqueued();
        return new EmailResult { IsSuccess = true };
    }

    public Task<EmailResult> SendVerificationEmailAsync(
        string to, string username, string verificationToken, CancellationToken cancellation)
        => EnqueueEmailAsync(
            to,
            "verify",
            verificationToken,
            true,
            emailType: "verification",
            idempotencyKey: $"verify:{to.Trim().ToUpperInvariant()}:{verificationToken}",
            cancellation);
}
