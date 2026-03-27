using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Core.Interfaces;
using Core.Interfaces.Cache;
using Core.Models.DTOs;
using Core.Models.Friend;
using Infrastructure.Models.DbContext;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// 好友关系业务逻辑服务
/// </summary>
public class FriendshipService(UserDbContext context, ICacheProvider cacheService, ILogger<FriendshipService> logger)
    : IFriendshipService
{
    private const string FriendListCacheKey = "FriendList_{0}";
    private const string RequestListCacheKey = "RequestList_{0}_{1}"; // {userId}_{requestType}
    private const string RelationshipCacheKey = "Relationship_{0}_{1}"; // {userId1}_{userId2}

    private const string CacheIncomingStr = nameof(FriendRequestType.Incoming);
    private const string CacheOutgoingStr = nameof(FriendRequestType.Outgoing);


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

        return await CreateOrUpdateRequestAsync(requesterId, targetUserId, message, ct)
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
        await using var transaction = await context.Database
            .BeginTransactionAsync(ct)
            .ConfigureAwait(false);

        try
        {
            requesterRecord.IsDeleted = false;
            requesterRecord.DeletedAt = null;

            await context.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
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
        string? message, CancellationToken ct)
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

        
        await using var transaction = await context.Database.BeginTransactionAsync(ct) .ConfigureAwait(false);
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

            await context.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            _logger.LogDebug("{name} --> 该操作被取消", nameof(CreateOrUpdateRequestAsync));
            throw;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            _logger.LogError(ex, "发送好友请求失败，RequesterId={RequesterId}, TargetUserId={TargetUserId}",
                requesterId, targetUserId);
            return SendFriendRequestResult.Failed(
                FriendshipOperationResultErrorCode.InternalSystemError,
                "操作失败，请稍后重试");
        }

        // 缓存清理
        try
        {
            await Task.WhenAll(
                cacheService.RemoveAsync(string.Format(RequestListCacheKey, targetUserId, CacheIncomingStr), ct),
                cacheService.RemoveAsync(string.Format(RequestListCacheKey, requesterId, CacheOutgoingStr), ct)
            ).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "发送好友请求后缓存清理失败");
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
                cacheService.RemoveAsync(string.Format(FriendListCacheKey, userId1), ct),
                cacheService.RemoveAsync(string.Format(FriendListCacheKey, userId2), ct),
                cacheService.RemoveAsync(string.Format(RequestListCacheKey, userId1, CacheIncomingStr), ct),
                cacheService.RemoveAsync(string.Format(RequestListCacheKey, userId2, CacheOutgoingStr), ct),
                cacheService.RemoveAsync(string.Format(RelationshipCacheKey, userId1, userId2), ct),
                cacheService.RemoveAsync(string.Format(RelationshipCacheKey, userId2, userId1), ct)
            ).ConfigureAwait(false);
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

        await using var transaction = await context.Database
            .BeginTransactionAsync(ct)
            .ConfigureAwait(false);

        try
        {
            request.Status = RequestStatus.Declined;
            request.RespondedAt = DateTime.UtcNow;

            if (blockAfterDecline)
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
            _logger.LogError(ex, "拒绝好友请求失败，DeclinerId={DeclinerId}, RequesterId={RequesterId}",
                declinerId, requesterId);
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.InternalSystemError,
                "操作失败，请稍后重试");
        }

        // 缓存清理
        try
        {
            var tasks = new List<Task>
            {
                cacheService.RemoveAsync(string.Format(RequestListCacheKey, declinerId, CacheIncomingStr), ct),
                cacheService.RemoveAsync(string.Format(RequestListCacheKey, requesterId, CacheOutgoingStr), ct)
            };

            if (blockAfterDecline)
            {
                tasks.Add(cacheService.RemoveAsync(string.Format(FriendListCacheKey, declinerId), ct));
                tasks.Add(cacheService.RemoveAsync(string.Format(FriendListCacheKey, requesterId), ct));
                tasks.Add(cacheService.RemoveAsync(string.Format(RelationshipCacheKey, declinerId, requesterId), ct));
                tasks.Add(cacheService.RemoveAsync(string.Format(RelationshipCacheKey, requesterId, declinerId), ct));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "拒绝好友请求后缓存清理失败");
        }

        return FriendshipOperationResult.Success(request.RequesterId.ToString());
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

        await using var transaction = await context.Database
            .BeginTransactionAsync(ct)
            .ConfigureAwait(false);

        try
        {
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
        }
        catch (OperationCanceledException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
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
    /// <typeparam name="T">返回类型</typeparam>
    /// <param name="userId">用户ID</param>
    /// <param name="func">投影表达式</param>
    /// <param name="ct"></param>
    /// <returns>好友列表</returns>
    public async IAsyncEnumerable<T> GetFriendsAsync<T>(long userId, Expression<Func<UserFriendEntry, T>> func,[EnumeratorCancellation] CancellationToken ct = default)
        where T : class
    {
        var friends = context.Friendships
            .Where(f => f.UserId == userId)
            .Include(f => f.Friend)
            .Select(func)
            .AsNoTracking();

        await foreach (var item in friends.AsAsyncEnumerable().WithCancellation(ct))
        {
            yield return item;
        }
    }

    /// <summary>
    /// 获取好友请求列表
    /// </summary>
    /// <typeparam name="T">返回类型</typeparam>
    /// <param name="userId">用户ID</param>
    /// <param name="func">投影表达式</param>
    /// <param name="requestType">请求类型（Incoming/Outgoing）</param>
    /// <param name="ct"></param>
    /// <returns>请求列表</returns>
    public async IAsyncEnumerable<T> GetRequestsAsync<T>(long userId, Expression<Func<FriendRequest, T>> func,
        FriendRequestType requestType,[EnumeratorCancellation] CancellationToken ct = default) where T : class
    {
        // 收到请求和发出请求共用一套投影逻辑，只是起始查询条件不同。
        var query = requestType switch
        {
            FriendRequestType.Incoming => context.FriendRequests.Where(r => r.TargetUserId == userId),
            FriendRequestType.Outgoing => context.FriendRequests.Where(r => r.RequesterId == userId),
            _ => throw new ArgumentOutOfRangeException(nameof(requestType))
        };

        var results = query
            .Where(r => r.Status == RequestStatus.Pending)
            .Include(r => r.Requester)
            .Include(r => r.TargetUser)
            .Select(func)
            .AsNoTracking();

        await foreach (var item in results.AsAsyncEnumerable().WithCancellation(ct))
        {
            yield return item;
        }
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新好友备注失败，UserId={UserId}, FriendId={FriendId}",
                userId, friendId);
            return FriendshipOperationResult.Failed(
                FriendshipOperationResultErrorCode.InternalSystemError,
                "操作失败，请稍后重试");
        }

        try
        {
            await cacheService.RemoveAsync(string.Format(FriendListCacheKey, userId), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "更新备注后缓存清理失败");
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

        var originalGroup = friendship.GroupId;

        try
        {
            friendship.GroupId = groupId;
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "分配好友到分组失败，UserId={UserId}, FriendId={FriendId}, GroupId={GroupId}",
                userId, friendId, groupId);
            return FriendshipOperationResult.Failed( FriendshipOperationResultErrorCode.InternalSystemError, "操作失败，请稍后重试");
        }

        // 缓存清理
        try
        {
            await Task.WhenAll(
                cacheService.RemoveAsync(string.Format(FriendListCacheKey, userId), ct),
                cacheService.RemoveAsync($"FriendGroup_{userId}_{originalGroup}", ct),
                cacheService.RemoveAsync($"FriendGroup_{userId}_{groupId}", ct)
            ).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "分配分组后缓存清理失败");
        }

        return FriendshipOperationResult.Success();
    }

    /// <summary>
    /// 搜索好友
    /// </summary>
    /// <param name="userId">用户ID</param>
    /// <param name="searchTerm">搜索词</param>
    /// <param name="ct"></param>
    /// <returns>搜索结果</returns>
    /// <exception cref="ArgumentNullException"></exception>
    public async IAsyncEnumerable<FriendSearchResultDto> SearchFriendsAsync(long userId, string searchTerm,[EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(searchTerm) || searchTerm.Length < 2)
            throw new ArgumentException("搜索关键词至少需要 2 个字符。");

        // 对通配符做转义，避免用户输入 % 时把 Like 查询放大。  
        var safeSearchTerm = searchTerm.Replace("%", @"\%").Replace("_", @"\_");
        var cleanSearchTerm = $"%{safeSearchTerm}%";

        var query = from f in context.Friendships
            // 匹配 UserId
            where  f.UserId == userId
            // 模糊搜索
            where EF.Functions.ILike(f.Friend!.UserName!, cleanSearchTerm) ||
                  EF.Functions.ILike(f.Note!, cleanSearchTerm)
            // 按照用户名排序
            orderby f.Friend!.UserName
            select new FriendSearchResultDto
            {
                FriendId = f.Friend!.Id,
                FriendName = f.Friend.UserName,
                Note = f.Note,
                LastInteractionAt = f.Friend.LastLoginDate
            };

        await foreach (var item in query.AsNoTracking().AsAsyncEnumerable().WithCancellation(ct))
        {
            yield return item;
        }
    }


    /// <summary>
    /// 获取被指定用户封禁的用户列表
    /// </summary>
    /// <typeparam name="T">选择器返回的对象类型</typeparam>
    /// <param name="userId">查询封禁记录的用户ID</param>
    /// <param name="selector">用于从BlockRecord实体中选择和转换数据的表达式</param>
    /// <param name="ct">用于取消操作的CancellationToken</param>
    /// <returns>返回一个异步可枚举集合，包含根据选择器转换后的被封禁用户信息</returns>
    public async IAsyncEnumerable<T> GetBlockedUsersAsync<T>(
        long userId,
        Expression<Func<BlockRecord, T>> selector,
        [EnumeratorCancellation] CancellationToken ct = default) where T : class
    {
        var query = context.BlockRecords
            .Where(b => b.BlockerId == userId)
            .Include(b => b.BlockedUser)     // 需确认导航属性名
            .Select(selector)
            .AsNoTracking();

        await foreach (var item in query.AsAsyncEnumerable().WithCancellation(ct))
        {
            yield return item;
        }
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
}