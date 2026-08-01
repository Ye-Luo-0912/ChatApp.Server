using System.Linq.Expressions;
using Core.Interfaces;
using Core.Interfaces.Cache;
using Core.Models.Common;
using Core.Models.Friend;
using Core.Models.Identity;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// 好友关系业务逻辑服务。
/// </summary>
/// <remarks>
/// <para>P0-6 好友域唯一状态机（A 与 B 的成对逻辑状态，由 UserFriendEntry + BlockRecord + FriendRequest 派生）：</para>
/// <list type="bullet">
/// <item><c>None</c>：无任何关系。</item>
/// <item><c>Pending(A -&gt; B)</c>：A 已向 B 发起申请，B 未处理。</item>
/// <item><c>Accepted</c>：双向好友关系成立（双方 UserFriendEntry 均 IsDeleted=false）。</item>
/// <item><c>BlockedByA</c>：A 拉黑 B（存在 BlockRecord(A-&gt;B)），B 未拉黑 A。</item>
/// <item><c>BlockedByB</c>：B 拉黑 A，A 未拉黑 B。</item>
/// <item><c>BlockedMutual</c>：双方互拉黑。</item>
/// <item><c>Removed</c>：曾为 Accepted，至少一方软删（IsDeleted=true）且未拉黑。</item>
/// </list>
/// <para>合法迁移与副作用：</para>
/// <list type="bullet">
/// <item><c>None -&gt; Pending(A-&gt;B)</c>：SendRequest，前置拒绝任一方向拉黑。</item>
/// <item><c>Pending(A-&gt;B) -&gt; Accepted</c>：Accept，事务内重新校验拉黑、建立双向关系、关闭反方向 pending、写 Outbox。</item>
/// <item><c>Any -&gt; BlockedByX / BlockedMutual</c>：Block，事务内关闭双方 pending、软删 friendship。</item>
/// <item><c>BlockedByX -&gt; None/Removed</c>：Unblock，仅删除 BlockRecord，不自动恢复历史 friendship，需重新申请。</item>
/// <item><c>Accepted -&gt; Removed</c>：DeleteFriendship，单边软删；双方均删则物理删。</item>
/// </list>
/// <para>Everyone 自动接受策略：申请创建、接受、建立双向关系、关闭反方向 pending、Outbox 必须在单一 PostgreSQL 事务内完成，失败整体回滚不残留 pending。</para>
/// </remarks>
public class FriendshipService(
    UserDbContext context,
    IDerivedCache cacheService,
    ILogger<FriendshipService> logger,
    ISecurityNotificationService? securityNotifications = null)
    : IFriendshipService
{
    private const string RelationshipCacheKey = "Relationship_{0}_{1}"; // {userId1}_{userId2}

    private const int DefaultPageLimit = 50;
    private const int MaxPageLimit = 100;


    private readonly ILogger<FriendshipService> _logger = logger;

    /// <summary>
    /// Starts the transaction that owns a relationship write and, for PostgreSQL,
    /// acquires the pair advisory lock on that same transaction connection.
    /// </summary>
    /// <remarks>
    /// A session-scoped lock on a separately opened NpgsqlConnection used to hold
    /// one pool slot while this DbContext checked out another. Transaction-scoped
    /// advisory locking provides the same cross-instance serialization without
    /// that double-connection pattern, and PostgreSQL releases it on commit or
    /// rollback. The in-memory provider does not implement transactions, so it
    /// remains a deliberate no-op for unit-level validation tests.
    /// </remarks>
    private async Task<PairWriteTransaction> BeginPairWriteTransactionAsync(
        long userId1, long userId2, CancellationToken ct)
    {
        if (string.Equals(
                context.Database.ProviderName,
                "Microsoft.EntityFrameworkCore.InMemory",
                StringComparison.Ordinal))
            return PairWriteTransaction.Noop;

        var transaction = await context.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            if (string.Equals(
                    context.Database.ProviderName,
                    "Npgsql.EntityFrameworkCore.PostgreSQL",
                    StringComparison.Ordinal))
            {
                var key = ComputePairLockKey(userId1, userId2);
                await context.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT pg_advisory_xact_lock({key});", ct)
                    .ConfigureAwait(false);
            }

            return new PairWriteTransaction(transaction);
        }
        catch
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static long ComputePairLockKey(long userId1, long userId2)
    {
        var low = Math.Min(userId1, userId2);
        var high = Math.Max(userId1, userId2);
        unchecked
        {
            ulong hash = 14695981039346656037UL;
            for (var shift = 0; shift < 64; shift += 8)
            {
                hash ^= (byte)((ulong)low >> shift);
                hash *= 1099511628211UL;
            }

            for (var shift = 0; shift < 64; shift += 8)
            {
                hash ^= (byte)((ulong)high >> shift);
                hash *= 1099511628211UL;
            }

            return (long)hash;
        }
    }

    private sealed class PairWriteTransaction(IDbContextTransaction? transaction) : IAsyncDisposable
    {
        public static PairWriteTransaction Noop { get; } = new(null);

        public Task CommitAsync(CancellationToken ct) =>
            transaction?.CommitAsync(ct) ?? Task.CompletedTask;

        public Task RollbackAsync() =>
            transaction?.RollbackAsync(CancellationToken.None) ?? Task.CompletedTask;

        public ValueTask DisposeAsync() =>
            transaction?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private static bool HasValidPairIds(long userId1, long userId2) =>
        userId1 > 0 && userId2 > 0 && userId1 != userId2;

    private static string? NormalizeMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        return message.Trim();
    }

    private static bool IsValidMessage(string? message) =>
        message is null || message.Length <= FriendshipInputLimits.FriendRequestMessageMaxLength;

    private static bool IsValidNote(string? note) =>
        !string.IsNullOrWhiteSpace(note)
        && note.Trim().Length <= FriendshipInputLimits.FriendNoteMaxLength;

    /// <summary>
    /// 发送好友请求
    /// </summary>
    /// <param name="requesterId">发起请求的用户ID</param>
    /// <param name="targetUserId">目标用户的ID</param>
    /// <param name="message">可选的消息内容</param>
    /// <param name="ct">用于取消操作的CancellationToken</param>
    /// <returns>返回发送好友请求的结果，包含操作是否成功、错误码及消息等信息</returns>
    public async Task<SendFriendRequestResult> SendRequestAsync(
        long requesterId, long targetUserId,
        string? message = null, CancellationToken ct = default)
    {
        if (requesterId <= 0 || targetUserId <= 0)
            return SendFriendRequestResult.Failed(
                FriendshipOperationResultErrorCode.ValidationFailed,
                "用户 ID 必须为正数");

        if (requesterId == targetUserId)
            return SendFriendRequestResult.Failed(
                FriendshipOperationResultErrorCode.ValidationFailed,
                "不能添加自己为好友");

        if (!IsValidMessage(message))
            return SendFriendRequestResult.Failed(
                FriendshipOperationResultErrorCode.ValidationFailed,
                $"好友申请消息不能超过 {FriendshipInputLimits.FriendRequestMessageMaxLength} 个字符");

        message = NormalizeMessage(message);

        await using var transaction = await BeginPairWriteTransactionAsync(requesterId, targetUserId, ct)
            .ConfigureAwait(false);

        try
        {
            var targetUser = await context.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == targetUserId, ct)
                .ConfigureAwait(false);
            if (targetUser is null)
                return SendFriendRequestResult.Failed(
                    FriendshipOperationResultErrorCode.ValidationFailed,
                    "目标用户不存在");

            if (targetUser.LockoutEnabled && targetUser.LockoutEnd > DateTimeOffset.UtcNow)
                return SendFriendRequestResult.Failed(
                    FriendshipOperationResultErrorCode.ValidationFailed,
                    "目标账户不可用");

            var blockedEitherDirection = await context.BlockRecords.AsNoTracking()
                .AnyAsync(b => (b.BlockerId == requesterId && b.BlockedUserId == targetUserId)
                            || (b.BlockerId == targetUserId && b.BlockedUserId == requesterId), ct)
                .ConfigureAwait(false);
            if (blockedEitherDirection)
                return SendFriendRequestResult.Failed(
                    FriendshipOperationResultErrorCode.RequestAlreadyBlocked,
                    "存在拉黑关系，无法发送好友申请");

            if (targetUser.FriendRequestPolicy == FriendRequestPolicy.NoStrangers)
            {
                var alreadyFriends = await context.Friendships.AsNoTracking()
                    .AnyAsync(f => f.UserId == targetUserId && f.FriendId == requesterId && !f.IsDeleted, ct)
                    .ConfigureAwait(false);
                if (!alreadyFriends)
                    return SendFriendRequestResult.Failed(
                        FriendshipOperationResultErrorCode.FriendRequestRejectedByPrivacy,
                        "对方不允许陌生人添加好友");
            }

            var relationships = await context.Friendships
                .IgnoreQueryFilters()
                .Where(f => (f.UserId == requesterId && f.FriendId == targetUserId) ||
                            (f.UserId == targetUserId && f.FriendId == requesterId))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var requesterRecord = relationships.FirstOrDefault(f => f.UserId == requesterId);
            var targetRecord = relationships.FirstOrDefault(f => f.UserId == targetUserId);

            if (requesterRecord is { IsDeleted: false })
                return SendFriendRequestResult.Failed(
                    FriendshipOperationResultErrorCode.FriendshipAlreadyExists,
                    "你们已经是好友了");

            SendFriendRequestResult result;
            var incomingRequest = await context.FriendRequests
                .AnyAsync(r =>
                    r.RequesterId == targetUserId &&
                    r.TargetUserId == requesterId &&
                    r.Status == RequestStatus.Pending, ct)
                .ConfigureAwait(false);

            if (incomingRequest)
            {
                var acceptResult = await AcceptRequestLockedAsync(requesterId, targetUserId, ct)
                    .ConfigureAwait(false);
                result = !acceptResult.Succeeded
                    ? SendFriendRequestResult.Failed(acceptResult.ErrorCode, acceptResult.Message)
                    : SendFriendRequestResult.Success(
                        SendFriendRequestOutcome.AcceptedDirectly,
                        "对方已请求添加，已自动成为好友",
                        acceptResult.Data);
            }
            else if (requesterRecord is { IsDeleted: true } && targetRecord is { IsDeleted: false })
            {
                result = await RestoreFriendshipAsync(requesterRecord, ct)
                    .ConfigureAwait(false);
            }
            else if (targetUser.FriendRequestPolicy == FriendRequestPolicy.Everyone)
            {
                result = await AcceptEveryoneLockedAsync(
                        requesterId, targetUserId, message, targetUser.NotifyFriendRequests, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                result = await CreateOrUpdateRequestAsync(
                        requesterId, targetUserId, message, targetUser.NotifyFriendRequests, ct)
                    .ConfigureAwait(false);
            }

            if (!result.IsSuccess)
                return result;

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            await SafeClearCacheAsync(requesterId, targetUserId).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException)
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
            _logger.LogError(ex, "发送好友请求失败，RequesterId={RequesterId}, TargetUserId={TargetUserId}",
                requesterId, targetUserId);
            return SendFriendRequestResult.Failed(
                FriendshipOperationResultErrorCode.InternalSystemError,
                "操作失败，请稍后重试");
        }
    }

    /// <summary>
    /// 恢复已删除的好友关系
    /// </summary>
    /// <param name="requesterRecord">请求者的用户好友记录</param>
    /// <param name="requesterId">发起恢复请求的用户ID</param>
    /// <param name="targetUserId">目标用户的ID</param>
    /// <param name="ct">用于取消操作的CancellationToken</param>
    /// <returns>返回恢复好友关系的结果，包含操作是否成功、错误码及消息等信息</returns>
    private async Task<SendFriendRequestResult> RestoreFriendshipAsync(
        UserFriendEntry requesterRecord, CancellationToken ct)
    {
        requesterRecord.IsDeleted = false;
        requesterRecord.DeletedAt = null;

        await context.SaveChangesAsync(ct).ConfigureAwait(false);

        // 加载导航属性用于返回 DTO
        await context.Entry(requesterRecord)
            .Reference(f => f.Friend)
            .LoadAsync(ct)
            .ConfigureAwait(false);

        var dto = new FriendDto
        {
            FriendId = requesterRecord.FriendId,
            FriendName = requesterRecord.Friend?.UserName,
            AvatarUrl = requesterRecord.Friend?.AvatarUrl,
            GroupId = requesterRecord.GroupId,
            GroupName = requesterRecord.Group?.GroupName,
            CreatedAt = requesterRecord.CreatedAt,
            Note = requesterRecord.Note
        };

        return SendFriendRequestResult.Success(SendFriendRequestOutcome.FriendshipRestored, "成功添加对方为好友", dto);
    }

    /// <summary>
    /// 创建或更新好友请求
    /// </summary>
    /// <param name="requesterId">发起请求的用户ID</param>
    /// <param name="targetUserId">目标用户的ID</param>
    /// <param name="message">可选的消息内容</param>
    /// <param name="ct">用于取消操作的CancellationToken</param>
    /// <returns>返回创建或更新好友请求的结果，包含操作是否成功、错误码及消息等信息</returns>
    private async Task<SendFriendRequestResult> CreateOrUpdateRequestAsync(
        long requesterId, long targetUserId,
        string? message, bool targetNotifiesFriendRequests, CancellationToken ct)
    {
        var existingRequest = await context.FriendRequests
            .FirstOrDefaultAsync(r =>
                r.RequesterId == requesterId &&
                r.TargetUserId == targetUserId, ct)
            .ConfigureAwait(false);

        if (existingRequest?.Status == RequestStatus.Pending)
        {
            return SendFriendRequestResult.Success(SendFriendRequestOutcome.RequestAlreadyPending, "好友请求已发送，请勿重复操作");
        }

        if (existingRequest is null)
        {
            await context.FriendRequests.AddAsync(new FriendRequest
            {
                RequesterId = requesterId,
                TargetUserId = targetUserId,
                Message = message,
                Status = RequestStatus.Pending,
                CreatedAt = DateTime.UtcNow
            }, ct).ConfigureAwait(false);
        }
        else
        {
            // 被拒绝过 → 复用旧记录
            existingRequest.Status = RequestStatus.Pending;
            existingRequest.Message = message;
            existingRequest.CreatedAt = DateTime.UtcNow;
            existingRequest.RespondedAt = null;
        }

        // 好友申请通知在同一事务内写入 NotificationOutbox，由 Worker 投递。
        // 不再使用 fire-and-forget（避免 DbContext 释放/并发使用/丢通知）。
        if (targetNotifiesFriendRequests && securityNotifications is not null)
        {
            securityNotifications.StageNotify(
                targetUserId, "FriendRequest", "新的好友申请",
                $"用户 {requesterId} 向你发送了好友申请。",
                preferEmail: false);
        }

        await context.SaveChangesAsync(ct).ConfigureAwait(false);

        return SendFriendRequestResult.Success(SendFriendRequestOutcome.RequestSent);
    }

    /// <summary>
    /// 安全清理缓存，失败只记日志。
    /// </summary>
    /// <remarks>
    /// P0 正确性：数据库事务提交后的缓存失效不能绑定客户端 CancellationToken（RequestAborted）。
    /// 若客户端断开导致 RequestAborted 取消，已提交事务仍必须尝试清理旧关系状态。
    /// Presence 另走数据库权威查询，此缓存仅服务可重建的非授权读取。
    /// 此处使用独立 500ms 短超时，不传播 OperationCanceledException（视为缓存清理失败，仅记日志）。
    /// </remarks>
    private async Task SafeClearCacheAsync(long userId1, long userId2)
    {
        using var cleanupCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            await cacheService.RemoveManyAsync(
                [
                    string.Format(RelationshipCacheKey, userId1, userId2),
                    string.Format(RelationshipCacheKey, userId2, userId1)
                ],
                cleanupCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cleanupCts.IsCancellationRequested)
        {
            _logger.LogWarning("缓存清理超过独立 500ms 预算，UserId1={UserId1}, UserId2={UserId2}", userId1, userId2);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "缓存清理失败，UserId1={UserId1}, UserId2={UserId2}",
                userId1, userId2);
        }
    }

    /// <summary>
    /// 接受好友请求
    /// </summary>
    /// <param name="acceptorId">接受者ID</param>
    /// <param name="requesterId">请求者ID</param>
    /// <param name="ct">取消令牌，用于取消操作</param>
    /// <returns>操作结果，包含成功或失败的信息及可能的FriendDto对象</returns>
    public async Task<FriendshipOperationResult<FriendDto>> AcceptRequestAsync(long acceptorId, long requesterId,
        CancellationToken ct = default)
    {
        if (!HasValidPairIds(acceptorId, requesterId))
            return FriendshipOperationResult<FriendDto>.Failed(
                FriendshipOperationResultErrorCode.ValidationFailed,
                "用户 ID 必须为正数，且不能是同一用户");

        await using var transaction = await BeginPairWriteTransactionAsync(acceptorId, requesterId, ct)
            .ConfigureAwait(false);
        try
        {
            var result = await AcceptRequestLockedAsync(acceptorId, requesterId, ct).ConfigureAwait(false);
            if (!result.Succeeded)
                return result;

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            await SafeClearCacheAsync(acceptorId, requesterId).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException)
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
            _logger.LogWarning("接受好友请求操作被取消，AcceptorId={AcceptorId}, RequesterId={RequesterId}", acceptorId,
                requesterId);
            throw;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
            _logger.LogError(ex, "执行接受好友请求出现错误，AcceptorId={AcceptorId}, RequesterId={RequesterId}", acceptorId,
                requesterId);
            return FriendshipOperationResult<FriendDto>.Failed(
                FriendshipOperationResultErrorCode.InternalSystemError,
                "操作失败，请稍后重试");
        }
    }

    /// <summary>由公开 Accept 或已持有同一 pair write transaction 的 Send 调用。</summary>
    private async Task<FriendshipOperationResult<FriendDto>> AcceptRequestLockedAsync(
        long acceptorId, long requesterId, CancellationToken ct)
    {
        //获取好友请求
        var request = await GetFriendRequestAsync(acceptorId, requesterId, RequestStatus.Pending, ct).ConfigureAwait(false);
        if (request == null)
            return FriendshipOperationResult<FriendDto>.Failed(
                FriendshipOperationResultErrorCode.FriendshipRequestNotFound, "没有对应的好友请求");


        // P0-6：接受时重新校验拉黑状态（拉黑可能在申请后发生，任一方向拉黑均拒绝接受）
        var blockedOnAccept = await context.BlockRecords.AsNoTracking()
            .AnyAsync(b => (b.BlockerId == acceptorId && b.BlockedUserId == requesterId)
                        || (b.BlockerId == requesterId && b.BlockedUserId == acceptorId), ct)
            .ConfigureAwait(false);
        if (blockedOnAccept)
            return FriendshipOperationResult<FriendDto>.Failed(
                FriendshipOperationResultErrorCode.RequestAlreadyBlocked,
                "存在拉黑关系，无法接受好友申请");

        UserFriendEntry acceptorRecord;
        UserFriendEntry requesterRecord;

        request.Status = RequestStatus.Accepted;
        request.RespondedAt = DateTime.UtcNow;

        // P0-6：建立双向关系（复用历史记录或新建），同一事务内关闭反方向 pending（双方互发场景）
        (acceptorRecord, requesterRecord) = await EnsureMutualRowsAsync(acceptorId, requesterId, ct)
            .ConfigureAwait(false);
        await CloseReversePendingAsync(acceptorId, requesterId, ct).ConfigureAwait(false);

        await context.Entry(acceptorRecord)
            .Reference(f => f.Friend)
            .LoadAsync(ct)
            .ConfigureAwait(false);

        await context.SaveChangesAsync(ct).ConfigureAwait(false);

        var dto = new FriendDto
        {
            FriendId = acceptorRecord.FriendId,
            FriendName = acceptorRecord.Friend?.UserName,
            AvatarUrl = acceptorRecord.Friend?.AvatarUrl,
            GroupId = acceptorRecord.GroupId,
            GroupName = acceptorRecord.Group?.GroupName,
            CreatedAt = acceptorRecord.CreatedAt,
            Note = acceptorRecord.Note
        };

        return FriendshipOperationResult<FriendDto>.Success(dto);
    }

    /// <summary>
    /// 拒绝好友请求
    /// </summary>
    /// <param name="declinerId">拒绝者ID</param>
    /// <param name="requesterId">请求者ID</param>
    /// <param name="blockAfterDecline">是否在拒绝后拉黑</param>
    /// <param name="ct"></param>
    /// <returns>操作结果</returns>
    /// <summary>
    /// 拒绝好友请求
    /// </summary>
    public async Task<FriendshipOperationResult> DeclineRequestAsync(
        long declinerId, long requesterId,
        bool blockAfterDecline = false, CancellationToken ct = default)
    {
        if (!HasValidPairIds(declinerId, requesterId))
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.ValidationFailed,
                "用户 ID 必须为正数，且不能是同一用户");

        await using var transaction = await BeginPairWriteTransactionAsync(declinerId, requesterId, ct)
            .ConfigureAwait(false);

        try
        {
            var request = await GetFriendRequestAsync(declinerId, requesterId, RequestStatus.Pending, ct)
                .ConfigureAwait(false);

            if (request == null)
                return FriendshipOperationResult.Failed(
                    FriendshipOperationResultErrorCode.FriendshipRequestExpired,
                    "好友请求不存在或已处理");

            request.Status = RequestStatus.Declined;
            request.RespondedAt = DateTime.UtcNow;

            if (blockAfterDecline)
            {
                var alreadyBlocked = await context.BlockRecords
                    .AnyAsync(b => b.BlockerId == declinerId && b.BlockedUserId == requesterId, ct)
                    .ConfigureAwait(false);
                if (!alreadyBlocked)
                {
                    await context.BlockRecords.AddAsync(new BlockRecord
                    {
                        BlockerId = declinerId,
                        BlockedUserId = requesterId,
                        BlockedAt = DateTime.UtcNow
                    }, ct).ConfigureAwait(false);
                }

                // P0-6：拒绝并拉黑时关闭双方 pending（含反方向 decliner->requester 的待处理申请）
                await ClosePendingForBlockAsync(declinerId, requesterId, ct).ConfigureAwait(false);

                var friendships = await context.Friendships
                    .IgnoreQueryFilters()
                    .Where(f => (f.UserId == declinerId && f.FriendId == requesterId) ||
                                (f.UserId == requesterId && f.FriendId == declinerId))
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                foreach (var friendship in friendships)
                {
                    friendship.IsDeleted = true;
                    friendship.DeletedAt = DateTime.UtcNow;
                }
            }

            await context.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);

            await SafeClearCacheAsync(declinerId, requesterId).ConfigureAwait(false);
            return FriendshipOperationResult.Success(request.RequesterId.ToString());
        }
        catch (OperationCanceledException)
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
            _logger.LogError(ex, "拒绝好友请求失败，DeclinerId={DeclinerId}, RequesterId={RequesterId}",
                declinerId, requesterId);
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.InternalSystemError,
                "操作失败，请稍后重试");
        }
    }

    /// <inheritdoc />
    public async Task<FriendshipOperationResult> WithdrawRequestAsync(
        long requesterId, long targetUserId, CancellationToken ct = default)
    {
        if (!HasValidPairIds(requesterId, targetUserId))
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.ValidationFailed,
                "用户 ID 必须为正数，且不能是同一用户");

        await using var transaction = await BeginPairWriteTransactionAsync(requesterId, targetUserId, ct)
            .ConfigureAwait(false);

        try
        {
            var request = await context.FriendRequests
                .FirstOrDefaultAsync(r =>
                    r.RequesterId == requesterId
                    && r.TargetUserId == targetUserId
                    && r.Status == RequestStatus.Pending, ct)
                .ConfigureAwait(false);

            if (request is null)
                return FriendshipOperationResult.Failed(
                    FriendshipOperationResultErrorCode.FriendshipRequestNotFound,
                    "没有待撤回的好友申请");

            request.Status = RequestStatus.Withdrawn;
            request.RespondedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            await SafeClearCacheAsync(requesterId, targetUserId).ConfigureAwait(false);
            return FriendshipOperationResult.Success();
        }
        catch (OperationCanceledException)
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
            _logger.LogError(ex, "撤回好友申请失败 Requester={RequesterId} Target={TargetUserId}",
                requesterId, targetUserId);
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.InternalSystemError, "操作失败，请稍后重试");
        }
    }

    /// <summary>
    /// 拉黑用户
    /// </summary>
    /// <param name="blockerId">拉黑者ID</param>
    /// <param name="targetUserId">目标用户ID</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>操作结果</returns>
    public async Task<FriendshipOperationResult> BlockUserAsync(
        long blockerId, long targetUserId, CancellationToken ct = default)
    {
        if (blockerId <= 0 || targetUserId <= 0)
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.ValidationFailed,
                "用户 ID 必须为正数");

        if (blockerId == targetUserId)
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.ValidationFailed,
                "不能拉黑自己");

        await using var transaction = await BeginPairWriteTransactionAsync(blockerId, targetUserId, ct)
            .ConfigureAwait(false);

        try
        {
            var targetExists = await context.Users.AsNoTracking()
                .AnyAsync(user => user.Id == targetUserId, ct)
                .ConfigureAwait(false);
            if (!targetExists)
                return FriendshipOperationResult.Failed(
                    FriendshipOperationResultErrorCode.ValidationFailed,
                    "目标用户不存在");

            if (await context.BlockRecords
                    .AnyAsync(b => b.BlockerId == blockerId && b.BlockedUserId == targetUserId, ct)
                    .ConfigureAwait(false))
                return FriendshipOperationResult.Success("已在黑名单中");

            await context.BlockRecords.AddAsync(new BlockRecord
            {
                BlockerId = blockerId,
                BlockedUserId = targetUserId,
                BlockedAt = DateTime.UtcNow
            }, ct).ConfigureAwait(false);

            var friendships = await context.Friendships
                .IgnoreQueryFilters()
                .Where(f => (f.UserId == blockerId && f.FriendId == targetUserId) ||
                            (f.UserId == targetUserId && f.FriendId == blockerId))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var friendship in friendships)
            {
                friendship.IsDeleted = true;
                friendship.DeletedAt = DateTime.UtcNow;
            }

            // P0-6：拉黑时关闭双方已有 pending 请求（blocker 发出的 Withdrawn，收到的 Declined）
            await ClosePendingForBlockAsync(blockerId, targetUserId, ct).ConfigureAwait(false);

            await context.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            await SafeClearCacheAsync(blockerId, targetUserId).ConfigureAwait(false);
            return FriendshipOperationResult.Success();
        }
        catch (OperationCanceledException)
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
            _logger.LogError(ex, "拉黑用户失败，BlockerId={BlockerId}, TargetUserId={TargetUserId}",
                blockerId, targetUserId);
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.InternalSystemError,
                "操作失败，请稍后重试");
        }
    }

    /// <summary>
    /// 解除拉黑
    /// </summary>
    /// <param name="unblockerId">解除者ID</param>
    /// <param name="targetUserId">目标用户ID</param>
    /// <param name="ct"></param>
    /// <returns>操作结果</returns>
    public async Task<FriendshipOperationResult> UnblockUserAsync(
        long unblockerId, long targetUserId, CancellationToken ct = default)
    {
        if (!HasValidPairIds(unblockerId, targetUserId))
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.ValidationFailed,
                "用户 ID 必须为正数，且不能是同一用户");

        await using var transaction = await BeginPairWriteTransactionAsync(unblockerId, targetUserId, ct)
            .ConfigureAwait(false);

        try
        {
            var blockRecord = await context.BlockRecords
                .FirstOrDefaultAsync(b => b.BlockerId == unblockerId && b.BlockedUserId == targetUserId, ct)
                .ConfigureAwait(false);

            if (blockRecord == null)
                return FriendshipOperationResult.Failed(
                    FriendshipOperationResultErrorCode.FriendshipNotFound,
                    "未找到拉黑记录");

            // P0-6：解除拉黑仅删除 BlockRecord，不自动恢复历史 friendship（需重新申请）。
            // 历史若为 Accepted 则保持 Removed 状态，由双方重新发起申请建立关系。
            context.BlockRecords.Remove(blockRecord);

            await context.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            await SafeClearCacheAsync(unblockerId, targetUserId).ConfigureAwait(false);
            return FriendshipOperationResult.Success();
        }
        catch (OperationCanceledException)
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
            _logger.LogError(ex, "解除拉黑失败，UnblockerId={UnblockerId}, TargetUserId={TargetUserId}",
                unblockerId, targetUserId);
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.InternalSystemError,
                "操作失败，请稍后重试");
        }
    }

    /// <summary>
    /// 删除好友关系
    /// </summary>
    /// <param name="userId">用户ID</param>
    /// <param name="friendId">好友ID</param>
    /// <returns>操作结果</returns>
    public async Task<FriendshipOperationResult> DeleteFriendshipAsync(
        long userId, long friendId, CancellationToken ct = default)
    {
        if (!HasValidPairIds(userId, friendId))
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.ValidationFailed,
                "用户 ID 必须为正数，且不能是同一用户");

        await using var transaction = await BeginPairWriteTransactionAsync(userId, friendId, ct)
            .ConfigureAwait(false);

        try
        {
            var myRecord = await context.Friendships
                .FirstOrDefaultAsync(f => f.UserId == userId && f.FriendId == friendId, ct)
                .ConfigureAwait(false);

            if (myRecord == null)
                return FriendshipOperationResult.Failed(
                    FriendshipOperationResultErrorCode.FriendshipNotFound,
                    "未找到对应的好友关系");

            var friendRecord = await context.Friendships
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(f => f.UserId == friendId && f.FriendId == userId, ct)
                .ConfigureAwait(false);

            if (friendRecord is { IsDeleted: true })
            {
                // 对方已删除 → 双向物理删除
                context.Friendships.Remove(myRecord);
                context.Friendships.Remove(friendRecord);
            }
            else
            {
                // 对方还没删 → 单边软删除
                myRecord.IsDeleted = true;
                myRecord.DeletedAt = DateTime.UtcNow;
            }

            await context.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            await SafeClearCacheAsync(userId, friendId).ConfigureAwait(false);
            return FriendshipOperationResult.Success();
        }
        catch (OperationCanceledException)
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
            _logger.LogError(ex, "删除好友失败，UserId={UserId}, FriendId={FriendId}",
                userId, friendId);
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.InternalSystemError,
                "操作失败，请稍后重试");
        }
    }

    /// <summary>
    /// 获取好友列表
    /// </summary>
    public async Task<CursorPage<FriendDto>> GetFriendsAsync(
        long userId, string? cursor = null, int limit = DefaultPageLimit, CancellationToken ct = default)
    {
        var pageSize = ClampLimit(limit);
        var cursorId = ParseCursor(cursor);

        var query = context.Friendships
            .Where(f => f.UserId == userId);

        if (cursorId.HasValue)
            query = query.Where(f => f.FriendId > cursorId.Value);

        var items = await query
            .OrderBy(f => f.FriendId)
            .Select(f => new FriendDto
            {
                FriendId = f.FriendId,
                FriendName = f.Friend!.UserName,
                Note = f.Note,
                CreatedAt = f.CreatedAt,
                GroupId = f.GroupId,
                GroupName = f.Group != null ? f.Group.GroupName : null,
                AvatarUrl = f.Friend!.AvatarUrl,
            })
            .Take(pageSize + 1)
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return BuildPage(items, pageSize, x => x.FriendId);
    }

    /// <summary>
    /// 获取好友请求列表
    /// </summary>
    public async Task<CursorPage<FriendRequestDto>> GetRequestsAsync(
        long userId, FriendRequestType requestType, string? cursor = null, int limit = DefaultPageLimit,
        CancellationToken ct = default)
    {
        var pageSize = ClampLimit(limit);
        var cursorId = ParseCursor(cursor);

        var query = requestType switch
        {
            FriendRequestType.Incoming => context.FriendRequests.Where(r => r.TargetUserId == userId),
            FriendRequestType.Outgoing => context.FriendRequests.Where(r => r.RequesterId == userId),
            _ => throw new ArgumentOutOfRangeException(nameof(requestType))
        };

        query = query.Where(r => r.Status == RequestStatus.Pending);

        if (cursorId.HasValue)
            query = query.Where(r => r.RequestId > cursorId.Value);

        var items = await query
            .OrderBy(r => r.RequestId)
            .Select(r => new FriendRequestDto
            {
                RequestId = r.RequestId,
                RequesterId = r.RequesterId,
                TargetUserId = r.TargetUserId,
                Message = r.Message,
                Status = r.Status,
                CreatedAt = r.CreatedAt,
            })
            .Take(pageSize + 1)
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return BuildPage(items, pageSize, x => x.RequestId);
    }

    /// <summary>
    /// 检查两个用户之间的关系
    /// </summary>
    /// <param name="userId1">用户1 ID</param>
    /// <param name="userId2">用户2 ID</param>
    /// <returns>关系信息</returns>
    public async Task<FriendshipStatusInfo> CheckRelationshipAsync(long userId1, long userId2, CancellationToken ct = default)
    {
        var isBlocked = await context.BlockRecords.AsNoTracking()
            .AnyAsync(b => (b.BlockerId == userId1 && b.BlockedUserId == userId2)
                        || (b.BlockerId == userId2 && b.BlockedUserId == userId1), ct)
            .ConfigureAwait(false);
        if (isBlocked)
        {
            return new FriendshipStatusInfo
            {
                IsBlocked = true,
                IsMutual = false,
                Status = FriendshipStatus.None,
            };
        }

        var cacheKey = string.Format(RelationshipCacheKey, userId1, userId2);
        // 关系状态变化频率不高，用短缓存可以减少重复查库。
        var cached = await cacheService.TryGetAsync<FriendshipStatusInfo>(cacheKey, ct).ConfigureAwait(false);
        if (cached.Found)
            return cached.Value!;

        var result = await CheckRelationshipCoreAsync(userId1, userId2);

        await cacheService.SetAsync(cacheKey, result, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// 批量检查 watcher 与多个 target 的关系状态。
    /// PR3: 使用 MGET + 批量 SQL，替代逐个查询的 N+1 模式。
    /// 100 目标：从最多 100 次 GET + 100 次 SQL 降为 1 次 MGET + 1 次 SQL。
    /// </summary>
    public async Task<IReadOnlyDictionary<long, FriendshipStatusInfo>> CheckRelationshipsAsync(
        long watcherUserId,
        IReadOnlyList<long> targetUserIds,
        CancellationToken ct = default)
        => await CheckRelationshipsCoreAsync(watcherUserId, targetUserIds, useCache: true, ct)
            .ConfigureAwait(false);

    public async Task<IReadOnlyDictionary<long, FriendshipStatusInfo>> CheckRelationshipsAuthoritativeAsync(
        long watcherUserId,
        IReadOnlyList<long> targetUserIds,
        CancellationToken ct = default)
        => await CheckRelationshipsCoreAsync(watcherUserId, targetUserIds, useCache: false, ct)
            .ConfigureAwait(false);

    private async Task<IReadOnlyDictionary<long, FriendshipStatusInfo>> CheckRelationshipsCoreAsync(
        long watcherUserId,
        IReadOnlyList<long> targetUserIds,
        bool useCache,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(targetUserIds);
        if (targetUserIds.Count == 0)
            return new Dictionary<long, FriendshipStatusInfo>();

        // 去重 + 排除自己
        var targets = targetUserIds.Where(id => id > 0 && id != watcherUserId).Distinct().ToList();
        if (targets.Count == 0)
            return new Dictionary<long, FriendshipStatusInfo>();

        // Block is always authoritative; a stale positive relationship cache can never bypass it.
        var blockRows = await context.BlockRecords.AsNoTracking()
            .Where(b => (b.BlockerId == watcherUserId && targets.Contains(b.BlockedUserId))
                        || (b.BlockedUserId == watcherUserId && targets.Contains(b.BlockerId)))
            .Select(b => new { b.BlockerId, b.BlockedUserId })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var blockedTargets = blockRows
            .Select(b => b.BlockerId == watcherUserId ? b.BlockedUserId : b.BlockerId)
            .ToHashSet();

        string[]? cacheKeys = null;
        IReadOnlyList<CacheLookup<FriendshipStatusInfo>>? cached = null;
        if (useCache)
        {
            cacheKeys = new string[targets.Count];
            for (var i = 0; i < targets.Count; i++)
                cacheKeys[i] = string.Format(RelationshipCacheKey, watcherUserId, targets[i]);

            cached = await cacheService.TryGetManyAsync<FriendshipStatusInfo>(cacheKeys, ct)
                .ConfigureAwait(false);
        }

        var result = new Dictionary<long, FriendshipStatusInfo>(targets.Count);
        var missedTargets = new List<long>(targets.Count);
        for (var i = 0; i < targets.Count; i++)
        {
            var targetId = targets[i];
            if (blockedTargets.Contains(targetId))
            {
                result[targetId] = new FriendshipStatusInfo
                {
                    IsBlocked = true,
                    IsMutual = false,
                    Status = FriendshipStatus.None,
                };
            }
            else if (cached is not null && cached[i].Found)
            {
                result[targetId] = cached[i].Value!;
            }
            else
            {
                missedTargets.Add(targetId);
            }
        }

        if (missedTargets.Count == 0)
            return result;

        // One set query plus O(N) dictionary construction; no per-target FirstOrDefault scans.
        var rows = await context.Friendships
            .IgnoreQueryFilters()
            .Where(f => (f.UserId == watcherUserId && missedTargets.Contains(f.FriendId)) ||
                        (missedTargets.Contains(f.UserId) && f.FriendId == watcherUserId))
            .Select(f => new { f.UserId, f.FriendId, f.IsDeleted, f.CreatedAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var rowByPair = rows.ToDictionary(static row => (row.UserId, row.FriendId));
        List<KeyValuePair<string, FriendshipStatusInfo>>? cacheWrites = useCache
            ? new List<KeyValuePair<string, FriendshipStatusInfo>>(missedTargets.Count)
            : null;

        foreach (var targetId in missedTargets)
        {
            var hasOutgoing = rowByPair.TryGetValue((watcherUserId, targetId), out var outgoing)
                              && !outgoing.IsDeleted;
            var hasIncoming = rowByPair.TryGetValue((targetId, watcherUserId), out var incoming)
                              && !incoming.IsDeleted;

            var info = hasOutgoing && hasIncoming
                ? new FriendshipStatusInfo
                {
                    IsMutual = true,
                    Status = FriendshipStatus.Approved,
                    EstablishedDate = outgoing!.CreatedAt,
                }
                : new FriendshipStatusInfo { IsMutual = false, Status = FriendshipStatus.None };

            result[targetId] = info;
            cacheWrites?.Add(new KeyValuePair<string, FriendshipStatusInfo>(
                string.Format(RelationshipCacheKey, watcherUserId, targetId), info));
        }

        if (cacheWrites is { Count: > 0 })
        {
            await cacheService.SetManyAsync(cacheWrites, TimeSpan.FromMinutes(5), ct)
                .ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// 更新好友备注
    /// </summary>
    /// <param name="userId">用户ID</param>
    /// <param name="friendId">好友ID</param>
    /// <param name="note">备注</param>
    /// <param name="ct"></param>
    /// <returns>操作结果</returns>
    public async Task<FriendshipOperationResult> UpdateFriendNoteAsync(long userId, long friendId, string note,
        CancellationToken ct = default)
    {
        if (!HasValidPairIds(userId, friendId))
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.ValidationFailed,
                "用户 ID 必须为正数，且不能是同一用户");

        if (string.IsNullOrWhiteSpace(note))
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.ValidationFailed,
                "备注不能为空");

        if (!IsValidNote(note))
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.ValidationFailed,
                $"好友备注不能超过 {FriendshipInputLimits.FriendNoteMaxLength} 个字符");

        var normalizedNote = note.Trim();

        var friendship = await context.Friendships
            .FirstOrDefaultAsync(f => f.UserId == userId && f.FriendId == friendId && !f.IsDeleted, ct)
            .ConfigureAwait(false);

        if (friendship == null)
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.FriendshipNotFound,
                "未找到好友关系");

        try
        {
            friendship.Note = normalizedNote;
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新好友备注失败，UserId={UserId}, FriendId={FriendId}",
                userId, friendId);
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.InternalSystemError,
                "操作失败，请稍后重试");
        }

        return FriendshipOperationResult.Success();
    }

    /// <summary>
    /// 分配好友到分组
    /// </summary>
    /// <param name="userId">用户ID</param>
    /// <param name="friendId">好友ID</param>
    /// <param name="groupId">分组ID</param>
    /// <param name="ct"></param>
    /// <returns>操作结果</returns>
    public async Task<FriendshipOperationResult> AssignFriendToGroupAsync(
        long userId, long friendId, int groupId, CancellationToken ct = default)
    {
        var groupExists = await context.FriendGroups
            .AnyAsync(g => g.GroupId == groupId && g.UserId == userId, ct)
            .ConfigureAwait(false);

        if (!groupExists)
            return FriendshipOperationResult.Failed(FriendshipOperationResultErrorCode.FriendGroupNotFound, "未找到好友分组");

        var friendship = await context.Friendships
            .FirstOrDefaultAsync(f => f.UserId == userId && f.FriendId == friendId && !f.IsDeleted, ct)
            .ConfigureAwait(false);

        if (friendship == null)
            return FriendshipOperationResult.Failed(FriendshipOperationResultErrorCode.FriendshipNotFound, "未找到好友关系");

        try
        {
            friendship.GroupId = groupId;
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "分配好友到分组失败，UserId={UserId}, FriendId={FriendId}, GroupId={GroupId}",
                userId, friendId, groupId);
            return FriendshipOperationResult.Failed(FriendshipOperationResultErrorCode.InternalSystemError, "操作失败，请稍后重试");
        }

        return FriendshipOperationResult.Success();
    }

    public async Task<FriendshipOperationResult<FriendGroupDto>> CreateGroupAsync(
        long userId, string groupName, CancellationToken ct = default)
    {
        var name = groupName.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return FriendshipOperationResult<FriendGroupDto>.Failed(
                FriendshipOperationResultErrorCode.ValidationFailed, "分组名称不能为空");

        if (await context.FriendGroups.AnyAsync(g => g.UserId == userId && g.GroupName == name, ct))
            return FriendshipOperationResult<FriendGroupDto>.Failed(
                FriendshipOperationResultErrorCode.FriendGroupNameConflict, "分组名称已存在");

        var maxSort = await context.FriendGroups
            .Where(g => g.UserId == userId)
            .Select(g => (int?)g.SortOrder)
            .MaxAsync(ct)
            .ConfigureAwait(false) ?? -1;

        var group = new FriendGroup
        {
            UserId = userId,
            GroupName = name,
            CreatedAt = DateTime.UtcNow,
            SortOrder = maxSort + 1,
            IsDefault = false,
        };

        context.FriendGroups.Add(group);
        try
        {
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (PostgresDbException.IsUniqueViolation(
                  ex, PostgresDbException.FriendGroupNameConstraint))
        {
            return FriendshipOperationResult<FriendGroupDto>.Failed(
                FriendshipOperationResultErrorCode.FriendGroupNameConflict, "分组名称已存在");
        }

        return FriendshipOperationResult<FriendGroupDto>.Success(new FriendGroupDto
        {
            GroupId = group.GroupId,
            GroupName = group.GroupName,
            CreatedAt = group.CreatedAt,
            MemberCount = 0,
            SortOrder = group.SortOrder,
            IsDefault = group.IsDefault,
        });
    }

    public async Task<IReadOnlyList<FriendGroupDto>> ListGroupsAsync(long userId, CancellationToken ct = default)
    {
        var groups = await context.FriendGroups.AsNoTracking()
            .Where(g => g.UserId == userId)
            .OrderBy(g => g.SortOrder)
            .ThenBy(g => g.CreatedAt)
            .Select(g => new FriendGroupDto
            {
                GroupId = g.GroupId,
                GroupName = g.GroupName,
                CreatedAt = g.CreatedAt,
                SortOrder = g.SortOrder,
                IsDefault = g.IsDefault,
                MemberCount = context.Friendships.Count(f =>
                    f.UserId == userId && f.GroupId == g.GroupId && !f.IsDeleted),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return groups;
    }

    public async Task<FriendshipOperationResult> ReorderGroupsAsync(
        long userId, IReadOnlyList<int> groupIdsInOrder, CancellationToken ct = default)
    {
        if (groupIdsInOrder.Count == 0)
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.ValidationFailed, "分组顺序不能为空");

        var groups = await context.FriendGroups
            .Where(g => g.UserId == userId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (groups.Count != groupIdsInOrder.Count
            || groups.Select(g => g.GroupId).Except(groupIdsInOrder).Any())
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.ValidationFailed, "分组列表不完整或不属于当前用户");

        for (var i = 0; i < groupIdsInOrder.Count; i++)
        {
            var g = groups.First(x => x.GroupId == groupIdsInOrder[i]);
            g.SortOrder = i;
        }

        try
        {
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重排好友分组失败 UserId={UserId}", userId);
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.InternalSystemError, "操作失败，请稍后重试");
        }

        return FriendshipOperationResult.Success();
    }

    public async Task<FriendshipOperationResult> SetDefaultGroupAsync(
        long userId, int groupId, CancellationToken ct = default)
    {
        var groups = await context.FriendGroups
            .Where(g => g.UserId == userId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var target = groups.FirstOrDefault(g => g.GroupId == groupId);
        if (target is null)
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.FriendGroupNotFound, "未找到好友分组");

        foreach (var g in groups)
            g.IsDefault = g.GroupId == groupId;

        try
        {
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "设置默认分组失败 UserId={UserId}", userId);
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.InternalSystemError, "操作失败，请稍后重试");
        }

        return FriendshipOperationResult.Success();
    }

    public async Task<FriendshipOperationResult> RenameGroupAsync(
        long userId, int groupId, string newName, CancellationToken ct = default)
    {
        var name = newName.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.ValidationFailed, "分组名称不能为空");

        var group = await context.FriendGroups
            .FirstOrDefaultAsync(g => g.GroupId == groupId && g.UserId == userId, ct)
            .ConfigureAwait(false);
        if (group is null)
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.FriendGroupNotFound, "未找到好友分组");

        if (await context.FriendGroups.AnyAsync(
                g => g.UserId == userId && g.GroupName == name && g.GroupId != groupId, ct))
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.FriendGroupNameConflict, "分组名称已存在");

        group.GroupName = name;
        try
        {
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (PostgresDbException.IsUniqueViolation(
                  ex, PostgresDbException.FriendGroupNameConstraint))
        {
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.FriendGroupNameConflict, "分组名称已存在");
        }

        return FriendshipOperationResult.Success();
    }

    public async Task<FriendshipOperationResult> DeleteGroupAsync(
        long userId, int groupId, CancellationToken ct = default)
    {
        var group = await context.FriendGroups
            .FirstOrDefaultAsync(g => g.GroupId == groupId && g.UserId == userId, ct)
            .ConfigureAwait(false);
        if (group is null)
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.FriendGroupNotFound, "未找到好友分组");

        await using var tx = await context.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await context.Friendships
                .Where(f => f.UserId == userId && f.GroupId == groupId && !f.IsDeleted)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.GroupId, (int?)null), ct)
                .ConfigureAwait(false);

            // ExecuteUpdate 不更新 ChangeTracker；同步本地实体，避免随后 SaveChanges 写回旧 GroupId
            foreach (var entry in context.ChangeTracker.Entries<UserFriendEntry>())
            {
                if (entry.Entity.UserId == userId && entry.Entity.GroupId == groupId)
                    entry.Entity.GroupId = null;
            }

            context.FriendGroups.Remove(group);
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return FriendshipOperationResult.Success();
    }

    public async Task<CursorPage<FriendDto>> GetFriendsInGroupAsync(
        long userId, int groupId, string? cursor = null, int limit = 50, CancellationToken ct = default)
    {
        var pageSize = ClampLimit(limit);
        var cursorId = ParseCursor(cursor);

        var query = context.Friendships
            .Where(f => f.UserId == userId && f.GroupId == groupId && !f.IsDeleted);

        if (cursorId.HasValue)
            query = query.Where(f => f.FriendId > cursorId.Value);

        var items = await query
            .OrderBy(f => f.FriendId)
            .Select(f => new FriendDto
            {
                FriendId = f.FriendId,
                FriendName = f.Friend!.UserName,
                Note = f.Note,
                CreatedAt = f.CreatedAt,
                GroupId = f.GroupId,
                GroupName = f.Group != null ? f.Group.GroupName : null,
                AvatarUrl = f.Friend!.AvatarUrl,
            })
            .Take(pageSize + 1)
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return BuildPage(items, pageSize, x => x.FriendId);
    }

    /// <summary>
    /// 搜索好友
    /// </summary>
    public async Task<CursorPage<FriendSearchResultDto>> SearchFriendsAsync(long userId, string searchTerm,
        string? cursor = null, int limit = DefaultPageLimit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(searchTerm) || searchTerm.Length < 2)
            throw new ArgumentException("搜索关键词至少需要 2 个字符。");

        var pageSize = ClampLimit(limit);
        var cursorId = ParseCursor(cursor);

        // 对通配符做转义，避免用户输入 % 时把 Like 查询放大。  
        var safeSearchTerm = searchTerm.Replace("%", @"\%").Replace("_", @"\_");
        var cleanSearchTerm = $"%{safeSearchTerm}%";

        var query = context.Friendships
            .Where(f => f.UserId == userId)
            .Where(f => EF.Functions.ILike(f.Friend!.UserName!, cleanSearchTerm) ||
                        EF.Functions.ILike(f.Note!, cleanSearchTerm));

        if (cursorId.HasValue)
            query = query.Where(f => f.FriendId > cursorId.Value);

        var items = await query
            .OrderBy(f => f.FriendId)
            .Select(f => new FriendSearchResultDto
            {
                FriendId = f.FriendId,
                FriendName = f.Friend!.UserName,
                Note = f.Note,
                LastInteractionAt = f.Friend!.LastLoginDate,
            })
            .Take(pageSize + 1)
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return BuildPage(items, pageSize, x => x.FriendId);
    }


    /// <summary>
    /// 获取被指定用户封禁的用户列表
    /// </summary>
    public async Task<CursorPage<BlockedUserDto>> GetBlockedUsersAsync(
        long userId,
        string? cursor = null,
        int limit = DefaultPageLimit,
        CancellationToken ct = default)
    {
        var pageSize = ClampLimit(limit);
        var cursorId = ParseCursor(cursor);

        var query = context.BlockRecords
            .Where(b => b.BlockerId == userId);

        if (cursorId.HasValue)
            query = query.Where(b => b.BlockedUserId > cursorId.Value);

        var items = await query
            .OrderBy(b => b.BlockedUserId)
            .Select(b => new BlockedUserDto
            {
                UserId = b.BlockedUserId,
                UserName = b.BlockedUser!.UserName,
                AvatarUrl = b.BlockedUser!.AvatarUrl,
                BlockedAt = b.BlockedAt,
            })
            .Take(pageSize + 1)
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return BuildPage(items, pageSize, x => x.UserId);
    }

    /// <summary>
    /// 获取指定状态的好友请求
    /// </summary>
    /// <param name="targetUserId">目标用户ID</param>
    /// <param name="requesterId">请求者ID</param>
    /// <param name="status">请求状态</param>
    /// <param name="ctx">取消令牌</param>
    /// <returns>如果找到符合条件的FriendRequest对象，则返回该对象；否则返回null</returns>
    private async Task<FriendRequest?> GetFriendRequestAsync(long targetUserId, long requesterId,
        RequestStatus status, CancellationToken ctx = default)
    {
        return await context.FriendRequests
            .FirstOrDefaultAsync(r =>
                r.RequesterId == requesterId && r.TargetUserId == targetUserId && r.Status == status, cancellationToken: ctx);
    }


    /// <summary>
    /// P0-6：建立/恢复双向好友关系记录。复用历史软删记录或新建，返回 (acceptorRecord, requesterRecord)。
    /// 调用方须已在事务内。
    /// </summary>
    private async Task<(UserFriendEntry AcceptorRecord, UserFriendEntry RequesterRecord)> EnsureMutualRowsAsync(
        long acceptorId, long requesterId, CancellationToken ct)
    {
        var existing = await context.Friendships.IgnoreQueryFilters()
            .Where(f => (f.UserId == acceptorId && f.FriendId == requesterId) ||
                        (f.UserId == requesterId && f.FriendId == acceptorId))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var acceptorRecord = existing.FirstOrDefault(f => f.UserId == acceptorId);
        var requesterRecord = existing.FirstOrDefault(f => f.UserId == requesterId);

        if (acceptorRecord != null)
        {
            acceptorRecord.IsDeleted = false;
            acceptorRecord.DeletedAt = null;
        }
        else
        {
            acceptorRecord = new UserFriendEntry
            {
                UserId = acceptorId,
                FriendId = requesterId,
                CreatedAt = DateTime.UtcNow
            };
            context.Friendships.Add(acceptorRecord);
        }

        if (requesterRecord != null)
        {
            requesterRecord.IsDeleted = false;
            requesterRecord.DeletedAt = null;
        }
        else
        {
            requesterRecord = new UserFriendEntry
            {
                UserId = requesterId,
                FriendId = acceptorId,
                CreatedAt = DateTime.UtcNow
            };
            context.Friendships.Add(requesterRecord);
        }

        return (acceptorRecord, requesterRecord);
    }

    /// <summary>
    /// P0-6：关闭反方向待处理申请（acceptor-&gt;requester）。接受 forward(requester-&gt;acceptor) 后，
    /// 反方向若也有 pending 应一并标记 Accepted，避免残留。调用方须已在事务内。
    /// </summary>
    private async Task CloseReversePendingAsync(long acceptorId, long requesterId, CancellationToken ct)
    {
        var reverse = await context.FriendRequests
            .Where(r => r.RequesterId == acceptorId
                && r.TargetUserId == requesterId
                && r.Status == RequestStatus.Pending)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (reverse is not null)
        {
            reverse.Status = RequestStatus.Accepted;
            reverse.RespondedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// P0-6：拉黑时关闭双方 pending 申请。blocker 发出的标记 Withdrawn，blocker 收到的标记 Declined。
    /// 调用方须已在事务内，随后统一 SaveChanges。
    /// </summary>
    private async Task ClosePendingForBlockAsync(long blockerId, long blockedUserId, CancellationToken ct)
    {
        var pending = await context.FriendRequests
            .Where(r => r.Status == RequestStatus.Pending
                && ((r.RequesterId == blockerId && r.TargetUserId == blockedUserId)
                    || (r.RequesterId == blockedUserId && r.TargetUserId == blockerId)))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var now = DateTime.UtcNow;
        foreach (var r in pending)
        {
            r.Status = r.RequesterId == blockerId ? RequestStatus.Withdrawn : RequestStatus.Declined;
            r.RespondedAt = now;
        }
    }

    /// <summary>
    /// P0-6：Everyone 自动接受——申请创建、通知 Outbox、接受、建立双向关系、关闭反方向 pending
    /// 全部在单一 PostgreSQL 事务内完成，失败整体回滚不残留 pending 申请。
    /// </summary>
    private async Task<SendFriendRequestResult> AcceptEveryoneLockedAsync(
        long requesterId, long targetUserId, string? message, bool targetNotifiesFriendRequests, CancellationToken ct)
    {
        // 接受方为 target，申请方为 requester。
        var request = await context.FriendRequests
            .FirstOrDefaultAsync(r => r.RequesterId == requesterId && r.TargetUserId == targetUserId, ct)
            .ConfigureAwait(false);
        if (request?.Status == RequestStatus.Pending)
            return SendFriendRequestResult.Success(SendFriendRequestOutcome.RequestAlreadyPending, "好友请求已发送，请勿重复操作");

        try
        {
            if (request is null)
            {
                request = new FriendRequest
                {
                    RequesterId = requesterId,
                    TargetUserId = targetUserId,
                    Message = message,
                    Status = RequestStatus.Pending,
                    CreatedAt = DateTime.UtcNow
                };
                context.FriendRequests.Add(request);
            }
            else
            {
                request.Status = RequestStatus.Pending;
                request.Message = message;
                request.CreatedAt = DateTime.UtcNow;
                request.RespondedAt = null;
            }

            if (targetNotifiesFriendRequests && securityNotifications is not null)
            {
                securityNotifications.StageNotify(
                    targetUserId, "FriendRequest", "新的好友申请",
                    $"用户 {requesterId} 向你发送了好友申请。",
                    preferEmail: false);
            }

            request.Status = RequestStatus.Accepted;
            request.RespondedAt = DateTime.UtcNow;

            var (acceptorRecord, requesterRecord) = await EnsureMutualRowsAsync(targetUserId, requesterId, ct)
                .ConfigureAwait(false);
            await CloseReversePendingAsync(targetUserId, requesterId, ct).ConfigureAwait(false);

            await context.Entry(requesterRecord)
                .Reference(f => f.Friend)
                .LoadAsync(ct)
                .ConfigureAwait(false);

            await context.SaveChangesAsync(ct).ConfigureAwait(false);

            var dto = new FriendDto
            {
                FriendId = requesterRecord.FriendId,
                FriendName = requesterRecord.Friend?.UserName,
                AvatarUrl = requesterRecord.Friend?.AvatarUrl,
                GroupId = requesterRecord.GroupId,
                GroupName = requesterRecord.Group?.GroupName,
                CreatedAt = requesterRecord.CreatedAt,
                Note = requesterRecord.Note
            };

            return SendFriendRequestResult.Success(
                SendFriendRequestOutcome.AcceptedDirectly,
                "对方允许所有人添加，已自动成为好友",
                dto);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Everyone 自动接受失败，RequesterId={RequesterId}, TargetUserId={TargetUserId}",
                requesterId, targetUserId);
            return SendFriendRequestResult.Failed(
                FriendshipOperationResultErrorCode.InternalSystemError,
                "操作失败，请稍后重试");
        }
    }

    /// <summary>
    /// 检查两个用户之间的关系状态
    /// </summary>
    /// <param name="userId1">第一个用户的ID</param>
    /// <param name="userId2">第二个用户的ID</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>表示两个用户之间关系状态的信息</returns>
    private async Task<FriendshipStatusInfo> CheckRelationshipCoreAsync(long userId1, long userId2, CancellationToken ct = default)
    {
        var isBlocked = await context.BlockRecords.AsNoTracking()
            .AnyAsync(b => (b.BlockerId == userId1 && b.BlockedUserId == userId2)
                        || (b.BlockerId == userId2 && b.BlockedUserId == userId1), ct)
            .ConfigureAwait(false);
        if (isBlocked)
        {
            return new FriendshipStatusInfo
            {
                IsBlocked = true,
                IsMutual = false,
                Status = FriendshipStatus.None,
            };
        }

        var relations = await context.Friendships.IgnoreQueryFilters()
            .Where(f => (f.UserId == userId1 && f.FriendId == userId2) ||
                        (f.UserId == userId2 && f.FriendId == userId1))
            .ToListAsync(cancellationToken: ct)
            .ConfigureAwait(false);

        var f1 = relations.FirstOrDefault(f => f.UserId == userId1 && f.FriendId == userId2);
        var f2 = relations.FirstOrDefault(f => f.UserId == userId2 && f.FriendId == userId1);

        var user1HasUser2 = f1 is { IsDeleted: false };
        var user2HasUser1 = f2 is { IsDeleted: false };

        if (user1HasUser2 && user2HasUser1)
        {
            return new FriendshipStatusInfo
            {
                IsMutual = true,
                Status = FriendshipStatus.Approved,
                EstablishedDate = f1?.CreatedAt
            };
        }

        return new FriendshipStatusInfo { IsMutual = false, Status = FriendshipStatus.None };
    }

    private static int ClampLimit(int limit) =>
        Math.Clamp(limit <= 0 ? DefaultPageLimit : limit, 1, MaxPageLimit);

    private static long? ParseCursor(string? cursor) =>
        long.TryParse(cursor, out var id) ? id : null;

    private static CursorPage<T> BuildPage<T>(
        List<T> items,
        int limit,
        Func<T, long> idSelector) where T : class
    {
        var hasMore = items.Count > limit;
        if (hasMore)
            items.RemoveAt(items.Count - 1);

        return new CursorPage<T>
        {
            Items = items,
            HasMore = hasMore,
            NextCursor = hasMore && items.Count > 0 ? idSelector(items[^1]).ToString() : null
        };
    }
}
