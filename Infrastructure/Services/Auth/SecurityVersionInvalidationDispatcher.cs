using System.Data.Common;
using System.Runtime.CompilerServices;
using Core.Interfaces.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services.Auth;

/// <summary>
/// Holds auth-fence invalidations until the transaction that advanced the
/// durable security version has actually committed.
/// </summary>
public sealed class SecurityVersionInvalidationDispatcher(
    IServiceScopeFactory scopeFactory,
    ILogger<SecurityVersionInvalidationDispatcher> logger)
{
    private readonly Dictionary<DbTransaction, Dictionary<long, long>> _pending =
        new(TransactionReferenceComparer.Instance);
    private readonly Lock _gate = new();

    public void Register(DbTransaction transaction, long userId, long securityVersion)
    {
        if (userId <= 0 || securityVersion <= 0)
            return;

        lock (_gate)
        {
            if (!_pending.TryGetValue(transaction, out var userIds))
            {
                userIds = [];
                _pending.Add(transaction, userIds);
            }

            if (!userIds.TryGetValue(userId, out var current)
                || securityVersion > current)
            {
                userIds[userId] = securityVersion;
            }
        }
    }

    public async Task CommitAsync(
        DbTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        Dictionary<long, long>? userIds;
        lock (_gate)
        {
            if (!_pending.Remove(transaction, out userIds))
                return;
        }

        // Commit has already completed. Invalidation is a derived-cache
        // operation and must never make a successful database commit look like
        // a failed request. Use a fresh scope because the originating context
        // is still completing its transaction callback.
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var snapshots = scope.ServiceProvider.GetService<IAuthSnapshotStore>();
            if (snapshots is null)
                return;

            foreach (var (userId, securityVersion) in userIds)
            {
                try
                {
                    await snapshots.InvalidateAsync(
                            userId,
                            minimumSecurityVersion: securityVersion,
                            cancellationToken: CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "SecurityVersion 已提交但 Auth Fence 失效失败 UserId={UserId}",
                        userId);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SecurityVersion 已提交但 Auth Fence 失效作用域创建失败");
        }
    }

    public void Rollback(DbTransaction transaction)
    {
        lock (_gate)
            _pending.Remove(transaction);
    }

    private sealed class TransactionReferenceComparer : IEqualityComparer<DbTransaction>
    {
        public static readonly TransactionReferenceComparer Instance = new();

        public bool Equals(DbTransaction? x, DbTransaction? y)
            => ReferenceEquals(x, y);

        public int GetHashCode(DbTransaction obj)
            => RuntimeHelpers.GetHashCode(obj);
    }
}
