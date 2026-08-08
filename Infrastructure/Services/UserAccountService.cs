using Core.Exceptions;
using Core.Interfaces;
using Core.Interfaces.Auth;
using Core.Models;
using Core.Models.Auth;
using Core.Models.Common;
using Core.Models.Email;
using Core.Models.Identity;
using Core.Models.Security;
using Core.Models.Token;
using Infrastructure.Data;
using Infrastructure.Services.Auth;
using Core.Models.User;
using Microsoft.EntityFrameworkCore;
using Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>
/// 处理用户资料查询、更新、删除和密码修改。
/// </summary>
public partial class UserAccountService(
    IUserRepository userRepository,
    UserDbContext db,
    IPasswordHasher passwordHasher,
    IEmailVerificationService emailVerificationService,
    ISessionStore sessionStore,
    IDeviceInfo deviceInfo,
    IAvatarStorage avatarStorage,
    ISecurityEventStore securityEventStore,
    ISecurityNotificationService securityNotifications,
    ITrustedDeviceService trustedDevices,
    IOptions<ProfileOptions> profileOptions,
    ILogger<UserAccountService> logger,
    ISecurityVersionAdvancer? securityVersions = null,
    IAttachmentBlobDeleteService? attachmentBlobDeletes = null,
    ISecurityMutationCoordinator? securityMutations = null,
    IPhoneVerificationService? phoneVerification = null,
    ITokenService? tokenService = null,
    IAvatarFinalizationSagaService? avatarFinalization = null) : IUserAccountService
{
    private readonly ProfileOptions _profile = profileOptions.Value;
    private readonly ISecurityMutationCoordinator _securityMutationCoordinator =
        securityMutations ?? new SecurityMutationCoordinator(
            db,
            securityVersions ?? new SecurityVersionAdvancer(db),
            NullLogger<SecurityMutationCoordinator>.Instance);
    private readonly IAttachmentBlobDeleteService? _attachmentBlobDeletes = attachmentBlobDeletes;
    private readonly IPhoneVerificationService? _phoneVerification = phoneVerification;
    private readonly ITokenService? _tokenService = tokenService;
    private readonly IAvatarFinalizationSagaService? _avatarFinalization = avatarFinalization;

    public async Task<UserProfileResponse?> GetByIdAsync(long userId, CancellationToken cancellationToken = default)
    {
        try
        {
            var user = await userRepository.FindByIdAsync(userId, cancellationToken);
            return user is null ? null : UserProfileResponse.FromUser(user);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "查找用户 ID {UserId} 时发生异常", userId);
            throw new IdentityException("用户查询失败", ex);
        }
    }

    public async Task<PublicUserResponse?> GetByUserNameAsync(string username, CancellationToken cancellationToken = default)
    {
        try
        {
            var user = await userRepository.FindByNameAsync(username, cancellationToken);
            return user is null ? null : PublicUserResponse.FromUser(user);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "查找用户 {Username} 时发生异常", username);
            throw new IdentityException("用户查询失败", ex);
        }
    }

    public Task<CursorPage<PublicUserSearchResult>> SearchUsersAsync(
        string searchTerm, string? cursor = null, int limit = 20, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(searchTerm) || searchTerm.Trim().Length < 2)
        {
            return Task.FromResult(new CursorPage<PublicUserSearchResult>
            {
                Items = [],
                HasMore = false,
                NextCursor = null,
            });
        }

        return userRepository.SearchUsersAsync(searchTerm.Trim(), cursor, limit, cancellationToken);
    }

    public async Task<AuthOperationResult?> UpdateAsync(
        long userId, UpdateProfileRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var user = await userRepository.FindByIdAsync(userId, cancellationToken);
            if (user is null)
                return null;

            if (request.PhoneNumber is not null)
            {
                var phoneResult = await RequestPhoneChangeAsync(
                        userId, request.PhoneNumber, cancellationToken)
                    .ConfigureAwait(false);
                if (phoneResult is null || !phoneResult.Succeeded)
                    return phoneResult;
            }

            if (request.Signature is not null)
                user.Signature = request.Signature.Length <= 500 ? request.Signature : request.Signature[..500];

            if (request.Region is not null)
                user.Region = request.Region.Length <= 200 ? request.Region : request.Region[..200];

            if (request.Birthday.HasValue)
                user.Birthday = request.Birthday;

            if (request.Gender.HasValue)
                user.Gender = request.Gender.Value;

            if (request.AllowBeSearched.HasValue)
                user.AllowBeSearched = request.AllowBeSearched.Value;

            if (request.NotifySecurityEmail.HasValue)
                user.NotifySecurityEmail = request.NotifySecurityEmail.Value;

            if (!string.IsNullOrWhiteSpace(request.UserName))
            {
                var nameResult = await TryChangeUserNameAsync(user, request.UserName.Trim(), cancellationToken);
                if (!nameResult.Succeeded)
                    return nameResult;
            }

            var ok = await userRepository.UpdateAsync(user, cancellationToken);
            if (ok) logger.LogInformation("成功更新用户 {UserId}", userId);

            return ok
                ? AuthOperationResult.Success()
                : AuthOperationResult.Fail("UpdateFailed", "用户信息更新失败");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "更新用户 {UserId} 时发生异常", userId);
            throw new IdentityException("用户更新失败", ex);
        }
    }

    public async Task<AvatarPresignResponse?> CreateAvatarUploadTicketAsync(
        long userId, string contentType, long contentLength, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.FindByIdAsync(userId, cancellationToken);
        if (user is null) return null;

        try
        {
            var (objectKey, ticket, uploadUrl, publicUrl, expiresAt) =
                await avatarStorage.CreateUploadTicketAsync(userId, contentType, contentLength, cancellationToken);
            return new AvatarPresignResponse
            {
                ObjectKey = objectKey,
                Ticket = ticket,
                UploadUrl = uploadUrl,
                PublicUrl = publicUrl,
                ExpiresAt = expiresAt,
                UploadHeaders = avatarStorage is IAvatarUploadHeadersProvider headersProvider
                    ? headersProvider.GetRequiredUploadHeaders(contentType)
                    : null,
            };
        }
        catch (ArgumentException)
        {
            throw;
        }
    }

    public async Task<AuthOperationResult?> ConfirmAvatarAsync(
        long userId, string objectKey, string? ticket = null, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.FindByIdAsync(userId, cancellationToken);
        if (user is null) return null;

        if (string.IsNullOrWhiteSpace(objectKey))
            return AuthOperationResult.Fail("InvalidObjectKey", "无效的头像对象键");

        if (_avatarFinalization is not null)
        {
            var requested = await _avatarFinalization.RequestAsync(
                    userId, objectKey, ticket, cancellationToken)
                .ConfigureAwait(false);
            return requested.Result;
        }

        var oldUrl = user.AvatarUrl;
        var (ok, publicUrl, finalObjectKey, error) = await avatarStorage.ConfirmObjectAsync(
            userId, objectKey, ticket, cancellationToken);
        if (!ok)
            return AuthOperationResult.Fail("ConfirmFailed", error ?? "头像确认失败");

        IReadOnlyList<string> candidateKeys = !string.IsNullOrWhiteSpace(objectKey)
                                               && !string.Equals(objectKey, finalObjectKey, StringComparison.Ordinal)
            ? new[] { objectKey }
            : Array.Empty<string>();
        var orphanKeys = new[] { objectKey, finalObjectKey }
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var afterCommitKeys = candidateKeys
            .Append(oldUrl)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return await CommitAvatarAsync(
                user,
                publicUrl,
                afterCommitKeys,
                orphanKeys,
                candidateKeys,
                publishedCandidateKeys: finalObjectKey is null
                    ? Array.Empty<string>()
                    : new[] { finalObjectKey },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AuthOperationResult?> UploadAvatarBytesAsync(
        long userId, string ticket, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.FindByIdAsync(userId, cancellationToken);
        if (user is null) return null;

        var oldUrl = user.AvatarUrl;
        var (ok, publicUrl, finalObjectKey, error) = await avatarStorage.StoreAsync(
            userId, ticket, content, contentType, cancellationToken);
        if (!ok)
            return AuthOperationResult.Fail("UploadFailed", error ?? "头像上传失败");

        if (_avatarFinalization is not null && !string.IsNullOrWhiteSpace(finalObjectKey))
        {
            var requested = await _avatarFinalization.RequestAsync(
                    userId, finalObjectKey, ticket: null, cancellationToken)
                .ConfigureAwait(false);
            if (!requested.Result.Succeeded)
            {
                await QueueAvatarDeletesAsync(
                        new[] { finalObjectKey, publicUrl }.OfType<string>(),
                        userId)
                    .ConfigureAwait(false);
            }

            return requested.Result;
        }

        var orphanKeys = new[] { finalObjectKey, publicUrl }
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var afterCommitKeys = new[] { oldUrl }
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return await CommitAvatarAsync(
                user,
                publicUrl,
                afterCommitKeys,
                orphanKeys,
                candidateKeys: Array.Empty<string>(),
                publishedCandidateKeys: finalObjectKey is null
                    ? Array.Empty<string>()
                    : new[] { finalObjectKey },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<AuthOperationResult> CommitAvatarAsync(
        ApplicationUser user,
        string? publicUrl,
        IReadOnlyList<string> afterCommitKeys,
        IReadOnlyList<string> orphanKeys,
        IReadOnlyList<string> candidateKeys,
        IReadOnlyList<string> publishedCandidateKeys,
        CancellationToken cancellationToken)
    {
        var candidateQueued = false;
        if (_attachmentBlobDeletes is not null && publishedCandidateKeys.Count > 0)
        {
            try
            {
                // Persist the candidate before opening the AvatarUrl
                // transaction. If the process exits between the object write
                // and the user update, the cleanup worker can reclaim the
                // object after the publication grace period instead of losing
                // the only durable reference when the user transaction rolls
                // back.
                await _attachmentBlobDeletes.EnqueueAvatarCandidatesAsync(
                        publishedCandidateKeys, user.Id, cancellationToken)
                    .ConfigureAwait(false);
                candidateQueued = true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "头像候选墓碑入队失败 UserId={UserId}", user.Id);
                await QueueAvatarDeletesAsync(orphanKeys, user.Id).ConfigureAwait(false);
                return AuthOperationResult.Fail("UpdateFailed", "头像保存失败");
            }
        }

        var ownsTransaction = db.Database.IsRelational()
                              && db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;

        var commitStarted = false;
        try
        {
            user.AvatarUrl = publicUrl;
            if (user.AvatarVersion == long.MaxValue)
                throw new InvalidOperationException("头像版本已达到最大值");
            user.AvatarVersion = Math.Max(1, user.AvatarVersion + 1);
            if (!await userRepository.UpdateAsync(user, cancellationToken).ConfigureAwait(false))
            {
                if (transaction is not null)
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                await ReleaseAvatarCandidatesAsync(
                        candidateQueued, publishedCandidateKeys, user.Id)
                    .ConfigureAwait(false);
                await QueueAvatarDeletesAsync(orphanKeys, user.Id).ConfigureAwait(false);
                return AuthOperationResult.Fail("UpdateFailed", "头像保存失败");
            }

            // A non-relational/test context has no explicit commit callback;
            // SaveChanges has already made the AvatarUrl durable there.
            if (transaction is null)
                commitStarted = true;

            // For a relational context this is part of the same transaction
            // as AvatarUrl. For non-relational test stores, UpdateAsync has
            // already committed, so a later failure must keep the candidate
            // tombstone rather than deleting a potentially referenced file.
            if (candidateQueued)
            {
                await _attachmentBlobDeletes!
                    .PublishAvatarCandidatesAsync(
                        publishedCandidateKeys, user.Id, cancellationToken)
                    .ConfigureAwait(false);
            }

            await QueueAvatarDeletesAsync(
                    afterCommitKeys,
                    user.Id,
                    cancellationToken,
                    propagateFailure: true)
                .ConfigureAwait(false);

            if (transaction is not null)
            {
                commitStarted = true;
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return AuthOperationResult.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (transaction is not null && !commitStarted)
            {
                try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
                catch { /* preserve the original cancellation */ }
                db.ChangeTracker.Clear();
                await ReleaseAvatarCandidatesAsync(
                        candidateQueued, publishedCandidateKeys, user.Id)
                    .ConfigureAwait(false);
                await QueueAvatarDeletesAsync(orphanKeys, user.Id).ConfigureAwait(false);
            }
            else if (transaction is null && !commitStarted)
            {
                await ReleaseAvatarCandidatesAsync(
                        candidateQueued, publishedCandidateKeys, user.Id)
                    .ConfigureAwait(false);
                await QueueAvatarDeletesAsync(orphanKeys, user.Id).ConfigureAwait(false);
            }
            else if (commitStarted)
            {
                await QueueAvatarDeletesAsync(candidateKeys, user.Id).ConfigureAwait(false);
            }
            throw;
        }
        catch (Exception ex)
        {
            if (transaction is not null && !commitStarted)
            {
                try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception rollbackEx) { logger.LogWarning(rollbackEx, "头像更新回滚失败 UserId={UserId}", user.Id); }
                db.ChangeTracker.Clear();
                await ReleaseAvatarCandidatesAsync(
                        candidateQueued, publishedCandidateKeys, user.Id)
                    .ConfigureAwait(false);
                await QueueAvatarDeletesAsync(orphanKeys, user.Id).ConfigureAwait(false);
            }
            else if (transaction is null && !commitStarted)
            {
                await ReleaseAvatarCandidatesAsync(
                        candidateQueued, publishedCandidateKeys, user.Id)
                    .ConfigureAwait(false);
                await QueueAvatarDeletesAsync(orphanKeys, user.Id).ConfigureAwait(false);
            }
            else
            {
                // A commit exception is ambiguous. The final key may already
                // be referenced by the committed row; only the never-
                // referenced candidate is safe to delete.
                await QueueAvatarDeletesAsync(candidateKeys, user.Id).ConfigureAwait(false);
            }

            logger.LogWarning(ex, "头像元数据提交失败 UserId={UserId}", user.Id);
            return AuthOperationResult.Fail("UpdateFailed", "头像保存失败");
        }
    }

    private async Task ReleaseAvatarCandidatesAsync(
        bool candidateQueued,
        IReadOnlyList<string> candidateKeys,
        long userId)
    {
        if (!candidateQueued || _attachmentBlobDeletes is null || candidateKeys.Count == 0)
            return;

        try
        {
            await _attachmentBlobDeletes
                .ReleaseAvatarCandidatesAsync(candidateKeys, userId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "头像候选墓碑释放失败 UserId={UserId}", userId);
        }
    }

    private async Task QueueAvatarDeletesAsync(
        IEnumerable<string> objectKeys,
        long userId,
        CancellationToken cancellationToken = default,
        bool propagateFailure = false)
    {
        var keys = objectKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (keys.Length == 0)
            return;

        try
        {
            if (_attachmentBlobDeletes is not null)
            {
                await _attachmentBlobDeletes
                    .EnqueueAvatarAsync(keys, userId, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            foreach (var key in keys)
                await avatarStorage.TryDeleteAsync(key, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "头像对象删除墓碑入队失败 UserId={UserId}", userId);
            if (propagateFailure)
                throw;
        }
    }

    public async Task<AuthOperationResult?> RequestPhoneChangeAsync(
        long userId, string newPhoneNumber, CancellationToken cancellationToken = default)
    {
        if (_phoneVerification is null)
            return AuthOperationResult.Fail("PhoneVerificationUnavailable", "手机号验证服务未配置");

        var normalized = PhoneNumberNormalizer.TryNormalizeE164(newPhoneNumber);
        if (normalized is null)
            return AuthOperationResult.Fail("InvalidPhoneNumber", "手机号必须是 E.164 格式，例如 +8613800138000");

        var user = await userRepository.FindByIdAsync(userId, cancellationToken);
        if (user is null)
            return null;
        if (string.Equals(user.NormalizedPhoneNumber, normalized, StringComparison.Ordinal)
            && user.PhoneNumberConfirmed)
            return AuthOperationResult.Fail("SamePhoneNumber", "新手机号与当前手机号相同");

        if (await db.Users.AsNoTracking()
                .AnyAsync(u => u.NormalizedPhoneNumber == normalized && u.Id != userId, cancellationToken)
                .ConfigureAwait(false))
            return AuthOperationResult.Fail("PhoneNumberTaken", "该手机号已被其他账户使用");

        user.PendingPhoneNumber = normalized;
        user.NormalizedPendingPhoneNumber = normalized;
        user.PendingPhoneRequestedAt = DateTimeOffset.UtcNow;
        if (!await userRepository.UpdateAsync(user, cancellationToken).ConfigureAwait(false))
            return AuthOperationResult.Fail("UpdateFailed", "无法保存待验证手机号");

        var sent = await _phoneVerification.SendCodeAsync(normalized, cancellationToken)
            .ConfigureAwait(false);
        return sent.Succeeded
            ? AuthOperationResult.Success()
            : AuthOperationResult.Fail("SendCodeFailed", sent.Error ?? "验证码发送失败");
    }

    public async Task<AuthOperationResult?> ConfirmPhoneChangeAsync(
        long userId, string code, CancellationToken cancellationToken = default)
    {
        if (_phoneVerification is null)
            return AuthOperationResult.Fail("PhoneVerificationUnavailable", "手机号验证服务未配置");

        PhoneVerificationClaim? claim = null;
        var committed = false;
        try
        {
            var user = await userRepository.FindByIdAsync(userId, cancellationToken);
            if (user is null)
                return null;
            if (string.IsNullOrWhiteSpace(user.NormalizedPendingPhoneNumber))
                return AuthOperationResult.Fail("NoPendingPhone", "没有待确认的手机号变更");

            var normalized = user.NormalizedPendingPhoneNumber;
            if (await db.Users.AsNoTracking()
                    .AnyAsync(u => u.NormalizedPhoneNumber == normalized && u.Id != userId, cancellationToken)
                    .ConfigureAwait(false))
                return AuthOperationResult.Fail("PhoneNumberTaken", "该手机号已被其他账户使用");

            var claimed = await _phoneVerification.ClaimCodeAsync(
                    normalized, code, cancellationToken)
                .ConfigureAwait(false);
            if (!claimed.Succeeded || claimed.Claim is null)
                return AuthOperationResult.Fail("InvalidCode", claimed.Error ?? "验证码无效");
            claim = claimed.Claim;

            user.PhoneNumber = normalized;
            user.NormalizedPhoneNumber = normalized;
            user.PhoneNumberConfirmed = true;
            user.PendingPhoneNumber = null;
            user.NormalizedPendingPhoneNumber = null;
            user.PendingPhoneRequestedAt = null;
            user.SecurityStamp = Guid.NewGuid().ToString();

            var mutation = await _securityMutationCoordinator.ExecuteAsync(
                    userId,
                    SecurityEventType.PhoneNumberChanged,
                    "phone-change-confirmed",
                    static _ => Task.CompletedTask,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!mutation.Succeeded)
                return AuthOperationResult.Fail("UpdateFailed", "手机号更新失败");

            committed = true;
            try
            {
                await _phoneVerification.CompleteCodeAsync(claim, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "手机号变更已提交但验证码完成清理失败 UserId={UserId}", userId);
            }

            // The security mutation and revocation outbox are already
            // committed. Redis cleanup is a derived effect; an outage must
            // not turn a successful phone change into a retryable 500.
            await RevokeAllSessionsSafelyAsync(userId).ConfigureAwait(false);
            return AuthOperationResult.Success();
        }
        catch (OperationCanceledException) { throw; }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException
                                           {
                                               SqlState: Npgsql.PostgresErrorCodes.UniqueViolation
                                           })
        {
            if (!committed && claim is not null)
                await _phoneVerification.RestoreCodeAsync(claim, CancellationToken.None).ConfigureAwait(false);
            logger.LogInformation(ex, "手机号变更发生唯一性冲突 UserId={UserId}", userId);
            return AuthOperationResult.Fail("PhoneNumberTaken", "该手机号已被其他账户使用");
        }
        catch (Exception ex)
        {
            if (!committed && claim is not null)
            {
                try
                {
                    await _phoneVerification.RestoreCodeAsync(claim, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception restoreError)
                {
                    logger.LogWarning(restoreError, "手机号变更失败后恢复验证码失败 UserId={UserId}", userId);
                }
            }
            logger.LogError(ex, "用户 {UserId} 确认手机号变更失败", userId);
            throw new IdentityException("确认手机号变更失败", ex);
        }
    }

    public async Task<AuthOperationResult?> CancelPhoneChangeAsync(
        long userId, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.FindByIdAsync(userId, cancellationToken);
        if (user is null)
            return null;

        user.PendingPhoneNumber = null;
        user.NormalizedPendingPhoneNumber = null;
        user.PendingPhoneRequestedAt = null;
        return await userRepository.UpdateAsync(user, cancellationToken).ConfigureAwait(false)
            ? AuthOperationResult.Success()
            : AuthOperationResult.Fail("UpdateFailed", "取消手机号变更失败");
    }

    public async Task<AuthOperationResult?> RequestEmailChangeAsync(long userId, string newEmail, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newEmail))
            return AuthOperationResult.Fail("InvalidEmail", "新邮箱不能为空");

        try
        {
            var user = await userRepository.FindByIdAsync(userId, cancellationToken);
            if (user is null)
                return null;

            var trimmed = newEmail.Trim();
            var normalized = trimmed.ToUpperInvariant();

            if (string.Equals(user.NormalizedEmail, normalized, StringComparison.Ordinal))
                return AuthOperationResult.Fail("SameEmail", "新邮箱与当前邮箱相同");

            if (await userRepository.IsEmailTakenAsync(normalized, userId, cancellationToken))
                return AuthOperationResult.Fail("EmailTaken", "该邮箱已被其他账户使用");

            user.PendingEmail = trimmed;
            user.NormalizedPendingEmail = normalized;
            user.PendingEmailRequestedAt = DateTimeOffset.UtcNow;

            var ok = await userRepository.UpdateAsync(user, cancellationToken);
            if (!ok)
                return AuthOperationResult.Fail("UpdateFailed", "无法保存待验证邮箱");

            var send = await emailVerificationService.SendEmailCodeAsync(
                trimmed, EmailCodePurpose.ChangeEmail, cancellationToken);
            if (!send.IsSuccess)
                return AuthOperationResult.Fail("SendCodeFailed", send.ErrorMessage ?? "验证码发送失败");

            logger.LogInformation("用户 {UserId} 发起邮箱变更，目标已写入 PendingEmail", userId);
            return AuthOperationResult.Success();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "用户 {UserId} 发起邮箱变更失败", userId);
            throw new IdentityException("邮箱变更请求失败", ex);
        }
    }

    public async Task<AuthOperationResult?> ConfirmEmailChangeAsync(long userId, string code, CancellationToken cancellationToken = default)
    {
        EmailVerificationClaim? emailClaim = null;
        var committed = false;
        try
        {
            var user = await userRepository.FindByIdAsync(userId, cancellationToken);
            if (user is null)
                return null;

            if (string.IsNullOrWhiteSpace(user.PendingEmail) || string.IsNullOrWhiteSpace(user.NormalizedPendingEmail))
                return AuthOperationResult.Fail("NoPendingEmail", "没有待确认的邮箱变更");

            var pending = user.PendingEmail;
            var normalizedPending = user.NormalizedPendingEmail;

            if (await userRepository.IsEmailTakenAsync(normalizedPending, userId, cancellationToken))
                return AuthOperationResult.Fail("EmailTaken", "该邮箱已被其他账户使用");

            var (verify, claim) = await emailVerificationService.ClaimEmailCodeAsync(
                pending, code, EmailCodePurpose.ChangeEmail, cancellationToken);
            if (!verify.IsSuccess)
                return AuthOperationResult.Fail("InvalidCode", verify.ErrorMessage ?? "验证码无效");
            emailClaim = claim;

            user.Email = pending;
            user.NormalizedEmail = normalizedPending;
            user.EmailConfirmed = true;
            user.PendingEmail = null;
            user.NormalizedPendingEmail = null;
            user.PendingEmailRequestedAt = null;
            user.SecurityStamp = Guid.NewGuid().ToString();

            var mutation = await _securityMutationCoordinator.ExecuteAsync(
                    userId,
                    SecurityEventType.EmailChanged,
                    "email-change-confirmed",
                    static _ => Task.CompletedTask,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!mutation.Succeeded)
            {
                await emailVerificationService.RestoreEmailCodeAsync(emailClaim!, cancellationToken);
                return AuthOperationResult.Fail("UpdateFailed", "邮箱更新失败");
            }
            committed = true;
            try
            {
                await emailVerificationService.CompleteEmailCodeAsync(
                    emailClaim!, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "邮箱变更已提交但验证码完成清理失败 UserId={UserId}", userId);
            }

            // SecurityVersion was advanced inside the same transaction. The
            // presented access/refresh token is therefore already invalid,
            // even if the Redis session row were kept. Revoke every session
            // explicitly so the product contract and the durable fence agree.
            // SecurityVersion + the durable revocation outbox are the
            // correctness boundary. Keep the request successful when the
            // best-effort Redis cleanup is temporarily unavailable.
            await RevokeAllSessionsSafelyAsync(userId).ConfigureAwait(false);

            logger.LogInformation("用户 {UserId} 邮箱已确认变更，已撤销全部设备会话", userId);
            return AuthOperationResult.Success();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (!committed && emailClaim is not null)
            {
                try
                {
                    await emailVerificationService.RestoreEmailCodeAsync(
                        emailClaim, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception restoreError)
                {
                    logger.LogWarning(restoreError, "邮箱变更失败后恢复验证码失败 UserId={UserId}", userId);
                }
            }
            logger.LogError(ex, "用户 {UserId} 确认邮箱变更失败", userId);
            throw new IdentityException("确认邮箱变更失败", ex);
        }
    }

    public async Task<AuthOperationResult?> CancelEmailChangeAsync(long userId, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.FindByIdAsync(userId, cancellationToken);
        if (user is null)
            return null;

        user.PendingEmail = null;
        user.NormalizedPendingEmail = null;
        user.PendingEmailRequestedAt = null;

        var ok = await userRepository.UpdateAsync(user, cancellationToken);
        return ok
            ? AuthOperationResult.Success()
            : AuthOperationResult.Fail("UpdateFailed", "取消邮箱变更失败");
    }

    public async Task<AuthOperationResult?> ChangePasswordAsync(
        long userId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        var response = await ChangePasswordCoreAsync(
                userId,
                currentPassword,
                newPassword,
                currentRefreshToken: null,
                currentSessionId: null,
                cancellationToken)
            .ConfigureAwait(false);
        return response?.Result;
    }

    public Task<SecurityMutationResponse?> ChangePasswordWithSessionAsync(
        long userId,
        string currentPassword,
        string newPassword,
        string? currentRefreshToken,
        string? currentSessionId,
        CancellationToken cancellationToken = default)
        => ChangePasswordCoreAsync(
            userId,
            currentPassword,
            newPassword,
            currentRefreshToken,
            currentSessionId,
            cancellationToken);

    private async Task<SecurityMutationResponse?> ChangePasswordCoreAsync(
        long userId,
        string currentPassword,
        string newPassword,
        string? currentRefreshToken,
        string? currentSessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var user = await userRepository.FindByIdAsync(userId, cancellationToken);
            if (user is null)
                return null;

            if (string.IsNullOrEmpty(user.PasswordHash)
                || !await passwordHasher.VerifyPasswordAsync(currentPassword, user.PasswordHash, cancellationToken))
                return SecurityMutationResponse.Fail("PasswordMismatch", "当前密码不正确");

            user.PasswordHash = await passwordHasher.HashPasswordAsync(newPassword, cancellationToken);
            user.PasswordHashVersion = passwordHasher.CurrentHashVersion;
            user.SecurityStamp = Guid.NewGuid().ToString();
            user.AccessFailedCount = 0;
            user.MustChangePassword = false;

            var mutation = await _securityMutationCoordinator.ExecuteAsync(
                    userId,
                    SecurityEventType.PasswordChanged,
                    "password-changed",
                    static _ => Task.CompletedTask,
                    cancellationToken,
                    securityEvent =>
                    {
                        securityEvent.DeviceId = deviceInfo.GetDeviceId();
                        securityEvent.ClientIp = deviceInfo.GenerateDeviceInfo().IpAddress;
                    },
                    new SecurityMutationOptions(
                        ExceptDeviceId: string.IsNullOrWhiteSpace(currentRefreshToken)
                            ? null
                            : deviceInfo.GetDeviceId(),
                        RevokeTrustedDevices: true))
                .ConfigureAwait(false);
            if (!mutation.Succeeded)
                return SecurityMutationResponse.Fail("UpdateFailed", "密码修改失败");

            var tokenPair = await ReissueCurrentSessionOrRevokeAllAsync(
                    user,
                    currentRefreshToken,
                    currentSessionId,
                    cancellationToken)
                .ConfigureAwait(false);
            await securityNotifications.NotifyAsync(
                userId, "PasswordChanged", "密码已修改",
                tokenPair is null
                    ? "您的账号密码已修改，全部设备会话已下线，全部可信设备已失效，请重新登录。"
                    : "您的账号密码已修改，其他设备会话已下线，可信设备已失效。",
                preferEmail: true,
                cancellationToken);

            logger.LogInformation(
                "用户 {UserId} 密码修改成功 CurrentSessionReissued={CurrentSessionReissued}",
                userId,
                tokenPair is not null);
            return SecurityMutationResponse.Success(tokenPair);
        }
        catch (OperationCanceledException) { throw; }
        catch (PasswordVerifyOverloadedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "修改用户 {UserId} 密码时发生异常", userId);
            throw new IdentityException("密码修改失败", ex);
        }
    }

    private async Task<TokenPairResult?> ReissueCurrentSessionOrRevokeAllAsync(
        ApplicationUser user,
        string? currentRefreshToken,
        string? currentSessionId,
        CancellationToken cancellationToken)
    {
        if (_tokenService is null
            || string.IsNullOrWhiteSpace(currentRefreshToken)
            || string.IsNullOrWhiteSpace(currentSessionId))
        {
            await RevokeAllSessionsSafelyAsync(user.Id).ConfigureAwait(false);
            return null;
        }

        try
        {
            var roles = await db.UserRoles
                .AsNoTracking()
                .Where(x => x.UserId == user.Id)
                .Select(x => x.Role.Name!)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var rotated = await _tokenService
                .ReissueSessionAfterSecurityMutationAsync(
                    user.Id.ToString(),
                    currentRefreshToken,
                    user,
                    roles,
                    currentSessionId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (rotated is null)
            {
                await RevokeAllSessionsSafelyAsync(user.Id).ConfigureAwait(false);
                return null;
            }

            await sessionStore.RevokeAllSessionsAsync(
                    user.Id.ToString(),
                    deviceInfo.GetDeviceId(),
                    cancellationToken)
                .ConfigureAwait(false);

            return TokenPairResult.Success(
                rotated.Value.AccessToken,
                rotated.Value.AccessTokenExpiresAtUtc,
                rotated.Value.RefreshToken,
                rotated.Value.RefreshTokenExpiresAtUtc,
                rotated.Value.DeviceCredential);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "密码修改后当前会话重签不确定，执行全量撤销 UserId={UserId}",
                user.Id);
            await RevokeAllSessionsSafelyAsync(user.Id).ConfigureAwait(false);
            return null;
        }
    }

    private async Task RevokeAllSessionsSafelyAsync(long userId)
    {
        try
        {
            await sessionStore.RevokeAllSessionsAsync(
                    userId.ToString(),
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The authorization fence has already advanced. If Redis is
            // unavailable, old tokens remain unusable by the auth fence; the
            // next durable retry can clean the derived session rows.
            logger.LogError(ex, "安全变更后的全量会话撤销失败 UserId={UserId}", userId);
        }
    }

    public async Task<AuthOperationResult?> DisableAsync(
        long userId, string? reason, long? actorUserId, CancellationToken cancellationToken = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await AdminRoleInvariant.AcquireMutationLockAsync(db, cancellationToken);
        if (db.Database.ProviderName?.Contains(
                "Npgsql", StringComparison.OrdinalIgnoreCase) == true)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""SELECT 1 FROM "AspNetUsers" WHERE "Id" = {userId} FOR UPDATE""",
                cancellationToken);
        }

        var user = await userRepository.FindByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            await tx.RollbackAsync(cancellationToken);
            return null;
        }

        if (await AdminRoleInvariant.IsLastActiveAdminAsync(db, userId, cancellationToken))
        {
            await tx.RollbackAsync(cancellationToken);
            return AuthOperationResult.Fail("LastAdmin", "不能禁用最后一个可用管理员");
        }

        user.LockoutEnabled = true;
        user.LockoutEnd = DateTimeOffset.MaxValue;
        user.SecurityStamp = Guid.NewGuid().ToString();

        var mutation = await _securityMutationCoordinator.ExecuteAsync(
                userId,
                SecurityEventType.AccountDisabled,
                reason,
                static _ => Task.CompletedTask,
                cancellationToken,
                securityEvent =>
                {
                    securityEvent.ActorUserId = actorUserId?.ToString();
                    if (actorUserId is { } actor)
                    {
                        db.AdminAuditLogs.Add(new AdminAuditLog
                        {
                            AdminUserId = actor,
                            TargetUserId = userId,
                            Action = "DisableUser",
                            Reason = reason,
                            ClientIp = deviceInfo.GenerateDeviceInfo().IpAddress,
                            CreatedAt = DateTimeOffset.UtcNow,
                        });
                    }
                })
            .ConfigureAwait(false);
        if (!mutation.Succeeded)
        {
            await tx.RollbackAsync(cancellationToken);
            return AuthOperationResult.Fail("UpdateFailed", "禁用账户失败");
        }

        await tx.CommitAsync(cancellationToken);

        try
        {
            await sessionStore.RevokeAllSessionsAsync(
                userId.ToString(), cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "禁用后撤销会话失败 UserId={UserId}", userId);
        }

        logger.LogWarning("用户 {UserId} 已被禁用并强制下线", userId);
        return AuthOperationResult.Success();
    }

    public async Task<AuthOperationResult?> EnableAsync(
        long userId, string? reason, long? actorUserId, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.FindByIdAsync(userId, cancellationToken);
        if (user is null)
            return null;

        user.LockoutEnd = null;
        user.AccessFailedCount = 0;
        user.SecurityStamp = Guid.NewGuid().ToString();

        var mutation = await _securityMutationCoordinator.ExecuteAsync(
                userId,
                SecurityEventType.AccountEnabled,
                reason,
                static _ => Task.CompletedTask,
                cancellationToken,
                securityEvent =>
                {
                    securityEvent.ActorUserId = actorUserId?.ToString();
                    if (actorUserId is { } actor)
                    {
                        db.AdminAuditLogs.Add(new AdminAuditLog
                        {
                            AdminUserId = actor,
                            TargetUserId = userId,
                            Action = "EnableUser",
                            Reason = reason,
                            ClientIp = deviceInfo.GenerateDeviceInfo().IpAddress,
                            CreatedAt = DateTimeOffset.UtcNow,
                        });
                    }
                })
            .ConfigureAwait(false);
        if (!mutation.Succeeded)
            return AuthOperationResult.Fail("UpdateFailed", "启用账户失败");

        return AuthOperationResult.Success();
    }

    public Task<CursorPage<DisabledUserDto>> ListDisabledUsersAsync(
        string? cursor = null, int limit = 50, CancellationToken cancellationToken = default)
        => userRepository.ListDisabledUsersAsync(cursor, limit, cancellationToken);

    public async Task<AuthOperationResult?> AssignRoleAsync(
        long userId, string roleName, long actorUserId, string? reason, CancellationToken cancellationToken = default)
    {
        if (!KnownRoles.IsAssignable(roleName))
            return AuthOperationResult.Fail("InvalidRole", "角色不在允许列表中");

        var outcome = await userRepository.MutateRoleAsync(
            userId, roleName, assign: true, actorUserId, reason,
            deviceInfo.GenerateDeviceInfo().IpAddress, cancellationToken);

        return await FinalizeRoleMutationAsync(userId, outcome, cancellationToken);
    }

    public async Task<AuthOperationResult?> RemoveRoleAsync(
        long userId, string roleName, long actorUserId, string? reason, bool confirmSelfDemotion = false,
        CancellationToken cancellationToken = default)
    {
        if (!KnownRoles.IsAssignable(roleName))
            return AuthOperationResult.Fail("InvalidRole", "角色不在允许列表中");

        if (actorUserId == userId
            && string.Equals(roleName.Trim(), KnownRoles.Admin, StringComparison.OrdinalIgnoreCase)
            && !confirmSelfDemotion)
        {
            return AuthOperationResult.Fail("ConfirmRequired", "撤销自己的 Admin 角色需要 ConfirmSelfDemotion=true");
        }

        var outcome = await userRepository.MutateRoleAsync(
            userId, roleName, assign: false, actorUserId, reason,
            deviceInfo.GenerateDeviceInfo().IpAddress, cancellationToken);

        return await FinalizeRoleMutationAsync(userId, outcome, cancellationToken);
    }

    private async Task<AuthOperationResult?> FinalizeRoleMutationAsync(
        long userId, RoleMutationOutcome outcome, CancellationToken cancellationToken)
    {
        switch (outcome)
        {
            case RoleMutationOutcome.UserNotFound:
                return null;
            case RoleMutationOutcome.RoleNotFound:
                return AuthOperationResult.Fail("RoleNotFound", "角色不存在");
            case RoleMutationOutcome.LastAdmin:
                return AuthOperationResult.Fail("LastAdmin", "不能撤销最后一个管理员");
            case RoleMutationOutcome.SecurityVersionFailed:
                return AuthOperationResult.Fail("RoleMutationFailed", "角色变更失败，用户安全版本无法推进");
            case RoleMutationOutcome.AlreadyHasRole:
            case RoleMutationOutcome.RoleNotAssigned:
            case RoleMutationOutcome.Success:
                break;
            default:
                return AuthOperationResult.Fail("RoleMutationFailed", "角色变更失败");
        }

        if (outcome is RoleMutationOutcome.Success)
        {
            await RevokeAllSessionsSafelyAsync(userId).ConfigureAwait(false);
            logger.LogWarning("用户 {UserId} 角色已变更，已撤销全部会话", userId);
            await securityNotifications.NotifyAsync(
                userId, "RoleChanged", "角色已变更",
                "您的账号角色已变更，全部会话已下线，请重新登录。",
                preferEmail: true, cancellationToken);
        }

        return AuthOperationResult.Success();
    }

    public async Task<IReadOnlyList<SessionDeviceProjection>> ListSessionsAsync(
        long userId, string? currentDeviceId, CancellationToken cancellationToken = default)
    {
        var sessions = await sessionStore.ListSessionsAsync(userId.ToString(), cancellationToken);
        return sessions
            .Select(s => SessionDeviceProjection.From(s, currentDeviceId is not null
                && string.Equals(s.DeviceId, currentDeviceId, StringComparison.Ordinal)))
            .OrderByDescending(s => s.LastActiveAt)
            .ToList();
    }

    public async Task RevokeSessionAsync(long userId, string deviceId, CancellationToken cancellationToken = default)
    {
        await sessionStore.RevokeSessionAsync(userId.ToString(), deviceId, cancellationToken);
        await securityEventStore.RecordAsync(
            userId, SecurityEventType.SessionRevoked, deviceId, detail: "用户撤销会话",
            cancellationToken: cancellationToken);
    }

    public Task<int> RevokeOtherSessionsAsync(long userId, string currentDeviceId, CancellationToken cancellationToken = default)
        => sessionStore.RevokeAllSessionsAsync(userId.ToString(), currentDeviceId, cancellationToken);

    public async Task<int> ForceLogoutAsync(
        long userId, string? reason, long? actorUserId, CancellationToken cancellationToken = default)
    {
        // Redis session deletion and Pub/Sub L1 eviction are deliberately
        // best-effort. The coordinator commits the durable user fence and
        // security event before those derived-store effects run.
        var mutation = await _securityMutationCoordinator.ExecuteAsync(
                userId,
                SecurityEventType.ForceLogout,
                reason,
                static _ => Task.CompletedTask,
                cancellationToken,
                securityEvent =>
                {
                    securityEvent.ActorUserId = actorUserId?.ToString();
                    if (actorUserId is { } actor)
                    {
                        db.AdminAuditLogs.Add(new AdminAuditLog
                        {
                            AdminUserId = actor,
                            TargetUserId = userId,
                            Action = "ForceLogout",
                            Reason = reason,
                            ClientIp = deviceInfo.GenerateDeviceInfo().IpAddress,
                            CreatedAt = DateTimeOffset.UtcNow,
                        });
                    }
                })
            .ConfigureAwait(false);
        if (!mutation.Succeeded)
            throw new IdentityException(
                "强制下线失败：用户安全版本无法推进",
                new InvalidOperationException("SecurityVersion advance returned no row"));

        try
        {
            return await sessionStore.RevokeAllSessionsAsync(
                    userId.ToString(),
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The committed security fence already invalidates old tokens;
            // the outbox will retry Redis session deletion. Do not report a
            // committed force-logout as a transport failure.
            logger.LogWarning(ex, "强制下线后的会话清理暂不可用 UserId={UserId}", userId);
            return 0;
        }
    }

    public Task<CursorPage<SecurityEventDto>> ListSecurityEventsAsync(
        long userId, string? cursor = null, int limit = 50, CancellationToken cancellationToken = default)
        => userRepository.ListSecurityEventsAsync(userId, cursor, limit, cancellationToken);

    public async Task<AuthOperationResult?> ReportNotMeAsync(
        long userId, long securityEventId, CancellationToken cancellationToken = default)
    {
        var evt = await userRepository.GetSecurityEventAsync(userId, securityEventId, cancellationToken);
        if (evt is null)
            return AuthOperationResult.Fail("NotFound", "安全事件不存在");

        var user = await userRepository.FindByIdAsync(userId, cancellationToken);
        if (user is null) return null;

        user.MustChangePassword = true;
        user.SecurityStamp = Guid.NewGuid().ToString();
        var mutation = await _securityMutationCoordinator.ExecuteAsync(
                userId,
                SecurityEventType.NotMeReported,
                $"sourceEvent={securityEventId}",
                static _ => Task.CompletedTask,
                cancellationToken,
                options: new SecurityMutationOptions(RevokeTrustedDevices: true))
            .ConfigureAwait(false);
        if (!mutation.Succeeded)
            return AuthOperationResult.Fail("UpdateFailed", "无法保存安全状态");

        // Derived stores are intentionally post-commit. SecurityVersion has
        // already invalidated old AT/RT/trusted-device credentials if any of
        // these best-effort operations are temporarily unavailable.
        try
        {
            if (!string.IsNullOrWhiteSpace(evt.DeviceId))
                await sessionStore.RevokeSessionAsync(
                        userId.ToString(), evt.DeviceId, CancellationToken.None)
                    .ConfigureAwait(false);

            await sessionStore.RevokeAllSessionsAsync(
                    userId.ToString(), cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "非本人操作后的会话清理暂不可用 UserId={UserId}", userId);
        }

        try
        {
            await trustedDevices.RevokeAllAsync(userId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "非本人操作后的可信设备清理暂不可用 UserId={UserId}", userId);
        }

        await securityNotifications.NotifyAsync(
            userId, "NotMeReported", "已标记非本人操作",
            "已撤销相关设备、可信设备，并要求修改密码。请立即通过“忘记密码”或登录后的改密流程更新密码。",
            preferEmail: true, cancellationToken);

        return AuthOperationResult.Success();
    }

    public async Task<AuthOperationResult?> RejectSuspiciousLoginAsync(
        long userId, long securityEventId, CancellationToken cancellationToken = default)
    {
        var evt = await userRepository.GetSecurityEventAsync(userId, securityEventId, cancellationToken);
        if (evt is null)
            return AuthOperationResult.Fail("NotFound", "安全事件不存在");

        if (evt.EventType is not (SecurityEventType.LoginUnusualLocation or SecurityEventType.LoginNewDevice
            or SecurityEventType.LoginSuccess))
            return AuthOperationResult.Fail("InvalidEvent", "仅可拒绝新设备/异常登录类事件");

        var user = await userRepository.FindByIdAsync(userId, cancellationToken);
        if (user is null) return null;

        // 优先撤销该次登录的设备会话；SessionId 是结构化列，不再从 Detail 解析。
        if (!string.IsNullOrWhiteSpace(evt.DeviceId))
            await sessionStore.RevokeSessionAsync(userId.ToString(), evt.DeviceId, cancellationToken);

        // 匹配 DeviceIdHint 的可信设备一并吊销（不核销全部）。
        if (!string.IsNullOrWhiteSpace(evt.DeviceId))
        {
            var devices = await trustedDevices.ListAsync(userId, cancellationToken);
            foreach (var d in devices.Where(d =>
                         string.Equals(d.DeviceIdHint, evt.DeviceId, StringComparison.Ordinal)))
            {
                await trustedDevices.RemoveAsync(userId, d.Id, cancellationToken);
            }
        }

        await securityEventStore.RecordAsync(
            userId, SecurityEventType.LoginRejected, evt.DeviceId, evt.ClientIp,
            detail: $"sourceEvent={securityEventId}",
            cancellationToken: cancellationToken,
            sessionId: evt.SessionId);

        await securityNotifications.NotifyAsync(
            userId, "LoginRejected", "已拒绝可疑登录",
            "已撤销该次登录关联的设备会话。若仍有异常，请使用「非本人」撤销全部会话并修改密码。",
            preferEmail: true, cancellationToken);

        return AuthOperationResult.Success();
    }

    private async Task<AuthOperationResult> TryChangeUserNameAsync(
        ApplicationUser user, string newName, CancellationToken cancellationToken)
    {
        if (newName.Length < _profile.UserNameMinLength || newName.Length > _profile.UserNameMaxLength)
            return AuthOperationResult.Fail("InvalidUserName", "用户名长度不符合要求");

        if (!IsValidUserNameCharacters(newName))
            return AuthOperationResult.Fail("InvalidUserName", "用户名仅允许字母、数字和下划线");

        if (string.Equals(user.UserName, newName, StringComparison.Ordinal))
            return AuthOperationResult.Success();

        var cooldown = TimeSpan.FromDays(Math.Max(1, _profile.UserNameCooldownDays));
        if (user.UserNameChangedAt is { } last
            && DateTimeOffset.UtcNow - last < cooldown)
        {
            var remain = cooldown - (DateTimeOffset.UtcNow - last);
            return AuthOperationResult.Fail("UserNameCooldown",
                $"用户名冷却中，约 {Math.Ceiling(remain.TotalDays)} 天后可再次修改");
        }

        var normalized = newName.ToUpperInvariant();
        if (await userRepository.IsUserNameTakenAsync(normalized, user.Id, cancellationToken))
            return AuthOperationResult.Fail("UserNameTaken", "用户名已被占用");

        user.UserName = newName;
        user.NormalizedUserName = normalized;
        user.UserNameChangedAt = DateTimeOffset.UtcNow;
        return AuthOperationResult.Success();
    }

    private static bool IsValidUserNameCharacters(string value)
    {
        foreach (var ch in value)
        {
            if (!((ch is >= 'a' and <= 'z')
                  || (ch is >= 'A' and <= 'Z')
                  || (ch is >= '0' and <= '9')
                  || ch == '_'))
                return false;
        }

        return value.Length > 0;
    }
}
