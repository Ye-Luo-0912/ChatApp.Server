using ChatApp.Server.IntegrationTests.Support;
using Core.Interfaces;
using Core.Models;
using Core.Models.Email;
using Infrastructure.Services;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Auth;

[Collection(nameof(RedisCollection))]
public sealed class EmailVerificationAtomicTests(RedisTestFixture redis)
{
    [Fact]
    public async Task VerifyEmailCodeAsync_ConcurrentCalls_OnlyOneSucceeds()
    {
        const string email = "atomic-verify@example.com";
        const string code = "123456";
        var purpose = EmailCodePurpose.Register;

        await redis.Cache.StringSetAsync(
            $"EmailCode:{purpose}:{email}",
            code,
            TimeSpan.FromMinutes(5));

        var service = new EmailVerificationService(new NoopEmailSender(), redis.Cache);

        var successes = 0;
        var failures = 0;

        var tasks = Enumerable.Range(0, 100).Select(async _ =>
        {
            var result = await service.VerifyEmailCodeAsync(email, code, purpose, CancellationToken.None);
            if (result.IsSuccess)
                Interlocked.Increment(ref successes);
            else
                Interlocked.Increment(ref failures);
        });

        await Task.WhenAll(tasks);

        Assert.Equal(1, successes);
        Assert.Equal(99, failures);
        Assert.Null(await redis.Cache.StringGetAsync($"EmailCode:{purpose}:{email}"));
    }

    [Fact]
    public async Task SendEmailCodeAsync_ConcurrentCalls_OnlyOneAcquiresCooldown()
    {
        const string email = "atomic-send@example.com";
        var purpose = EmailCodePurpose.Register;
        var sender = new CountingEmailSender();
        var service = new EmailVerificationService(sender, redis.Cache);

        var tasks = Enumerable.Range(0, 50)
            .Select(_ => service.SendEmailCodeAsync(email, purpose, CancellationToken.None));

        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r.IsSuccess));
        Assert.Equal(1, sender.SendCount);
        Assert.Equal(49, results.Count(r => !r.IsSuccess));
    }

    private sealed class NoopEmailSender : IEmailSender
    {
        public Task<EmailResult> SendEmailAsync(
            string to, string subject, string body, bool isHtml = true, CancellationToken cancellation = default)
            => Task.FromResult(new EmailResult { IsSuccess = true });

        public Task<EmailResult> SendVerificationEmailAsync(
            string to, string username, string verificationToken, CancellationToken cancellation)
            => Task.FromResult(new EmailResult { IsSuccess = true });
    }

    private sealed class CountingEmailSender : IEmailSender
    {
        private int _sendCount;
        public int SendCount => _sendCount;

        public Task<EmailResult> SendEmailAsync(
            string to, string subject, string body, bool isHtml = true, CancellationToken cancellation = default)
        {
            Interlocked.Increment(ref _sendCount);
            return Task.FromResult(new EmailResult { IsSuccess = true });
        }

        public Task<EmailResult> SendVerificationEmailAsync(
            string to, string username, string verificationToken, CancellationToken cancellation)
            => SendEmailAsync(to, "v", verificationToken, true, cancellation);
    }
}
