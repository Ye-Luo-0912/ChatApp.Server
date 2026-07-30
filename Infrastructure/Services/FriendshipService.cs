using System.Linq.Expressions;
using Core.Interfaces;
using Core.Interfaces.Cache;
using Core.Models.Common;
using Core.Models.Friend;
using Core.Models.Identity;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// 好友关系业务逻辑服务
/// </summary>
public class FriendshipService(
    UserDbContext context,
    ICacheValueStore cacheService,
    ILogger<FriendshipService> logger,
    ISecurityNotificationService? securityNotifications = null)
    : IFriendshipService
{
    private const string RelationshipCacheKey = "Relationship_{0}_{1}"; // {userId1}_{userId2}

    private const int DefaultPageLimit = 50;
    private const int MaxPageLimit = 100;


    private readonly ILogger<FriendshipService> _logger = logger;


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
        // 1. 基本校验 
        if (requesterId == targetUserId)
            return SendFriendRequestResult.Failed(
                FriendshipOperationResultErrorCode.ValidationFailed,
                "不能添加自己为好友");

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

        // 查询现有关系 
        var relationships = await context.Friendships
            .IgnoreQueryFilters()
            .Where(f => (f.UserId == requesterId && f.FriendId == targetUserId) ||
                        (f.UserId == targetUserId && f.FriendId == requesterId))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var requesterRecord = relationships.FirstOrDefault(f => f.UserId == requesterId);
        var targetRecord = relationships.FirstOrDefault(f => f.UserId == targetUserId);

        // 已经是好友
        if (requesterRecord is { IsDeleted: false })
            return SendFriendRequestResult.Failed(
                FriendshipOperationResultErrorCode.FriendshipAlreadyExists,
                "你们已经是好友了");

        // 对方已有待处理请求 → 自动接受 
        var incomingRequest = await context.FriendRequests
            .AnyAsync(r =>
                r.RequesterId == targetUserId &&
                r.TargetUserId == requesterId &&
                r.Status == RequestStatus.Pending, ct)
            .ConfigureAwait(false);

        if (incomingRequest)
        {
            var acceptResult = await AcceptRequestAsync(requesterId, targetUserId, ct)
                .ConfigureAwait(false);

            return !acceptResult.Succeeded
                ? SendFriendRequestResult.Failed(acceptResult.ErrorCode, acceptResult.Message)
                : SendFriendRequestResult.Success(
                    SendFriendRequestOutcome.AcceptedDirectly,
                    "对方已请求添加，已自动成为好友",
                    acceptResult.Data);
        }

        // 我删了对方，但对方没删我 → 直接恢复关系，无需走请求流程
        if (requesterRecord is { IsDeleted: true } && targetRecord is { IsDeleted: false })
        {
            return await RestoreFriendshipAsync(requesterRecord, requesterId, targetUserId, ct)
                .ConfigureAwait(false);
        }

        // Everyone：自动通过（创建待处理申请后立即以对方身份接受）
        if (targetUser.FriendRequestPolicy == FriendRequestPolicy.Everyone)
        {
            var create = await CreateOrUpdateRequestAsync(requesterId, targetUserId, message, targetUser.NotifyFriendRequests, ct)
                .ConfigureAwait(false);
            if (!create.IsSuccess
                && create.Outcome != SendFriendRequestOutcome.RequestAlreadyPending
                && create.Outcome != SendFriendRequestOutcome.RequestSent)
                return create;

            var accept = await AcceptRequestAsync(targetUserId, requesterId, ct).ConfigureAwait(false);
            return !accept.Succeeded
                ? SendFriendRequestResult.Failed(accept.ErrorCode, accept.Message)
                : SendFriendRequestResult.Success(
                    SendFriendRequestOutcome.AcceptedDirectly,
                    "对方允许所有人添加，已自动成为好友",
                    accept.Data);
        }

        return await CreateOrUpdateRequestAsync(requesterId, targetUserId, message, targetUser.NotifyFriendRequests, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 恢复已删除的好友关系
    /// </summary>
    /// <param name="requesterRecord">请求者的用户好友记录</param>
    /// <param name="requesterId">发起恢复请求的用户ID</param>
    /// <param name="targetUserId">目标用户的ID</param>
    /// <param name="ct">用于取消操作的CancellationToken</param>
    /// <returns>返回恢复好友关系的结果，包含操作是否成功、错误码及消息等信息</returns>
    private async Task<SendFriendRequestResult> RestoreFriendshipAsync(UserFriendEntry requesterRecord,
        long requesterId, long targetUserId, CancellationToken ct)
    {
        try
        {
            requesterRecord.IsDeleted = false;
            requesterRecord.DeletedAt = null;

            await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "恢复好友关系失败，RequesterId={RequesterId}, TargetUserId={TargetUserId}",
                requesterId, targetUserId);
            return SendFriendRequestResult.Failed(FriendshipOperationResultErrorCode.InternalSystemError, "操作失败，请稍后重试");
        }

        // 缓存清理（失败不影响业务结果）
        await SafeClearCacheAsync(requesterId, targetUserId, ct);

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
        //获取好友请求
        var existingRequest = await context.FriendRequests
            .FirstOrDefaultAsync(r =>
                r.RequesterId == requesterId &&
                r.TargetUserId == targetUserId, ct)
            .ConfigureAwait(false);
        
        if (existingRequest?.Status == RequestStatus.Pending)
        {
            return SendFriendRequestResult.Success(SendFriendRequestOutcome.RequestAlreadyPending, "好友请求已发送，请勿重复操作");
        }

        
        try
        {
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
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("{name} --> 该操作被取消", nameof(CreateOrUpdateRequestAsync));
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "发送好友请求失败，RequesterId={RequesterId}, TargetUserId={TargetUserId}",
                requesterId, targetUserId);
            return SendFriendRequestResult.Failed(
                FriendshipOperationResultErrorCode.InternalSystemError,
                "操作失败，请稍后重试");
        }

        return SendFriendRequestResult.Success(SendFriendRequestOutcome.RequestSent);
    }

    /// <summary>
    /// 安全清理缓存，失败只记日志
    /// </summary>
    private async Task SafeClearCacheAsync(
        long userId1, long userId2, CancellationToken ct)
    {
        try
        {
            await Task.WhenAll(
                cacheService.RemoveAsync(string.Format(RelationshipCacheKey, userId1, userId2), ct),
                cacheService.RemoveAsync(string.Format(RelationshipCacheKey, userId2, userId1), ct)
            ).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
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
        //获取好友请求
        var request = await GetFriendRequestAsync(acceptorId, requesterId, RequestStatus.Pending, ct).ConfigureAwait(false);
        if (request == null)
            return FriendshipOperationResult<FriendDto>.Failed(
                FriendshipOperationResultErrorCode.FriendshipRequestNotFound, "没有对应的好友请求");


        var existingRelationships = await context.Friendships.IgnoreQueryFilters()
            .Where(f => (f.UserId == acceptorId && f.FriendId == requesterId) ||
                        (f.UserId == requesterId && f.FriendId == acceptorId))
            .ToListAsync(cancellationToken: ct).ConfigureAwait(false);

        var acceptorRecord = existingRelationships.FirstOrDefault(f => f.UserId == acceptorId);
        var requesterRecord = existingRelationships.FirstOrDefault(f => f.UserId == requesterId);

        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        try
        {
            request.Status = RequestStatus.Accepted;
            request.RespondedAt = DateTime.UtcNow;
            var newRecords = new List<UserFriendEntry>();

            if (acceptorRecord != null)
            {
                // 如果历史记录存在，执行“复活”操作
                acceptorRecord.IsDeleted = false;
                acceptorRecord.DeletedAt = null;
            }
            else
            {
                // 只有真没记录时才新建
                acceptorRecord = new UserFriendEntry
                {
                    UserId = acceptorId,
                    FriendId = requesterId,
                    CreatedAt = DateTime.UtcNow
                };
                newRecords.Add(acceptorRecord);
            }

            // 处理 请求者 -> 接受者 的关系记录
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
                newRecords.Add(requesterRecord);
            }

            if (newRecords.Count > 0)
                await context.Friendships.AddRangeAsync(newRecords, ct).ConfigureAwait(false);

            await context.Entry(acceptorRecord)
                .Reference(f => f.Friend)
                .LoadAsync(ct)
                .ConfigureAwait(false);

            await context.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            _logger.LogWarning("接受好友请求操作被取消，AcceptorId={AcceptorId}, RequesterId={RequesterId}", acceptorId,
                requesterId);
            throw;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            _logger.LogError(ex, "执行接受好友请求出现错误，AcceptorId={AcceptorId}, RequesterId={RequesterId}", acceptorId,
                requesterId);
            return FriendshipOperationResult<FriendDto>.Failed(
                FriendshipOperationResultErrorCode.InternalSystemError,
                "操作失败，请稍后重试");
        }

        // 缓存清理：失败只记日志，不影响结果
        await SafeClearCacheAsync(acceptorId, requesterId, ct);

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
        var request = await GetFriendRequestAsync(declinerId, requesterId, RequestStatus.Pending, ct)
            .ConfigureAwait(false);

        if (request == null)
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.FriendshipRequestExpired,
                "好友请求不存在或已处理");

        try
        {
            request.Status = RequestStatus.Declined;
            request.RespondedAt = DateTime.UtcNow;

            if (blockAfterDecline)
            {
                await using var transaction = await context.Database
                    .BeginTransactionAsync(ct)
                    .ConfigureAwait(false);

                try
                {
                    await context.BlockRecords.AddAsync(new BlockRecord
                    {
                        BlockerId = declinerId,
                        BlockedUserId = requesterId,
                        BlockedAt = DateTime.UtcNow
                    }, ct).ConfigureAwait(false);

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

                    await context.SaveChangesAsync(ct).ConfigureAwait(false);
                    await transaction.CommitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            }
            else
            {
                await context.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "拒绝好友请求失败，DeclinerId={DeclinerId}, RequesterId={RequesterId}",
                declinerId, requesterId);
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.InternalSystemError,
                "操作失败，请稍后重试");
        }

        if (blockAfterDecline)
        {
            try
            {
                await Task.WhenAll(
                    cacheService.RemoveAsync(string.Format(RelationshipCacheKey, declinerId, requesterId), ct),
                    cacheService.RemoveAsync(string.Format(RelationshipCacheKey, requesterId, declinerId), ct)
                ).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "拒绝好友请求后缓存清理失败");
            }
        }

        return FriendshipOperationResult.Success(request.RequesterId.ToString());
    }

    /// <inheritdoc />
    public async Task<FriendshipOperationResult> WithdrawRequestAsync(
        long requesterId, long targetUserId, CancellationToken ct = default)
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

        try
        {
            request.Status = RequestStatus.Withdrawn;
            request.RespondedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "撤回好友申请失败 Requester={RequesterId} Target={TargetUserId}",
                requesterId, targetUserId);
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.InternalSystemError, "操作失败，请稍后重试");
        }

        return FriendshipOperationResult.Success();
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
        if (blockerId == targetUserId)
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.ValidationFailed,
                "不能拉黑自己");

        if (await context.BlockRecords
                .AnyAsync(b => b.BlockerId == blockerId && b.BlockedUserId == targetUserId, ct)
                .ConfigureAwait(false))
            return FriendshipOperationResult.Success("已在黑名单中");

        await using var transaction = await context.Database
            .BeginTransactionAsync(ct)
            .ConfigureAwait(false);

        try
        {
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

            await context.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            _logger.LogError(ex, "拉黑用户失败，BlockerId={BlockerId}, TargetUserId={TargetUserId}",
                blockerId, targetUserId);
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.InternalSystemError,
                "操作失败，请稍后重试");
        }

        await SafeClearCacheAsync(blockerId, targetUserId, ct);
        return FriendshipOperationResult.Success();
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
        var blockRecord = await context.BlockRecords
            .FirstOrDefaultAsync(b => b.BlockerId == unblockerId && b.BlockedUserId == targetUserId, ct)
            .ConfigureAwait(false);

        if (blockRecord == null)
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.FriendshipNotFound,
                "未找到拉黑记录");

        await using var transaction = await context.Database
            .BeginTransactionAsync(ct)
            .ConfigureAwait(false);

        try
        {
            context.BlockRecords.Remove(blockRecord);

            // 用 Change Tracker 替代 ExecuteUpdateAsync，避免事务中混用
            var friendships = await context.Friendships
                .IgnoreQueryFilters()
                .Where(f => (f.UserId == unblockerId && f.FriendId == targetUserId) ||
                            (f.UserId == targetUserId && f.FriendId == unblockerId))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var friendship in friendships)
            {
                friendship.IsDeleted = false;
                friendship.DeletedAt = null;
            }

            await context.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            _logger.LogError(ex, "解除拉黑失败，UnblockerId={UnblockerId}, TargetUserId={TargetUserId}",
                unblockerId, targetUserId);
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.InternalSystemError,
                "操作失败，请稍后重试");
        }

        await SafeClearCacheAsync(unblockerId, targetUserId, ct);
        return FriendshipOperationResult.Success();
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

        try
        {
            if (friendRecord is { IsDeleted: true })
            {
                await using var transaction = await context.Database
                    .BeginTransactionAsync(ct)
                    .ConfigureAwait(false);

                try
                {
                    // 对方已删除 → 双向物理删除
                    context.Friendships.Remove(myRecord);
                    context.Friendships.Remove(friendRecord);

                    await context.SaveChangesAsync(ct).ConfigureAwait(false);
                    await transaction.CommitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            }
            else
            {
                // 对方还没删 → 单边软删除
                myRecord.IsDeleted = true;
                myRecord.DeletedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除好友失败，UserId={UserId}, FriendId={FriendId}",
                userId, friendId);
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.InternalSystemError,
                "操作失败，请稍后重试");
        }

        await SafeClearCacheAsync(userId, friendId, ct);
        return FriendshipOperationResult.Success();
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
        var cacheKey = string.Format(RelationshipCacheKey, userId1, userId2);
        // 关系状态变化频率不高，用短缓存可以减少重复查库。
        var cachedResult = await cacheService.GetAsync<FriendshipStatusInfo>(cacheKey, cancellationToken: ct).ConfigureAwait(false);
        if (cachedResult != null) return cachedResult;

        var result = await CheckRelationshipCoreAsync(userId1, userId2);

        await cacheService.SetAsync(cacheKey, result, TimeSpan.FromMinutes(5), cancellationToken: ct).ConfigureAwait(false);
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
        if (string.IsNullOrWhiteSpace(note))
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.ValidationFailed,
                "备注不能为空");

        var friendship = await context.Friendships
            .FirstOrDefaultAsync(f => f.UserId == userId && f.FriendId == friendId && !f.IsDeleted, ct)
            .ConfigureAwait(false);

        if (friendship == null)
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.FriendshipNotFound,
                "未找到好友关系");

        try
        {
            friendship.Note = note.Trim();
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
            return FriendshipOperationResult.Failed( FriendshipOperationResultErrorCode.FriendGroupNotFound, "未找到好友分组");

        var friendship = await context.Friendships
            .FirstOrDefaultAsync(f => f.UserId == userId && f.FriendId == friendId && !f.IsDeleted, ct)
            .ConfigureAwait(false);

        if (friendship == null)
            return FriendshipOperationResult.Failed( FriendshipOperationResultErrorCode.FriendshipNotFound, "未找到好友关系");

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
            return FriendshipOperationResult.Failed( FriendshipOperationResultErrorCode.InternalSystemError, "操作失败，请稍后重试");
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
    /// 检查两个用户之间的关系状态
    /// </summary>
    /// <param name="userId1">第一个用户的ID</param>
    /// <param name="userId2">第二个用户的ID</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>表示两个用户之间关系状态的信息</returns>
    private async Task<FriendshipStatusInfo> CheckRelationshipCoreAsync(long userId1, long userId2, CancellationToken ct =  default)
    {
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
