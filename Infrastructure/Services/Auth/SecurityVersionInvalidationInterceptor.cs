using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Infrastructure.Services.Auth;

/// <summary>
/// Bridges EF transaction lifecycle events to the post-commit auth-fence
/// invalidation dispatcher. Pub/Sub is intentionally sent only after commit.
/// </summary>
public sealed class SecurityVersionInvalidationInterceptor(
    SecurityVersionInvalidationDispatcher dispatcher) : DbTransactionInterceptor
{
    public override void TransactionCommitted(
        DbTransaction transaction,
        TransactionEndEventData eventData)
    {
        dispatcher.CommitAsync(transaction).GetAwaiter().GetResult();
        base.TransactionCommitted(transaction, eventData);
    }

    public override async Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await dispatcher.CommitAsync(transaction, cancellationToken).ConfigureAwait(false);
        await base.TransactionCommittedAsync(transaction, eventData, cancellationToken)
            .ConfigureAwait(false);
    }

    public override void TransactionRolledBack(
        DbTransaction transaction,
        TransactionEndEventData eventData)
    {
        dispatcher.Rollback(transaction);
        base.TransactionRolledBack(transaction, eventData);
    }

    public override Task TransactionRolledBackAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        dispatcher.Rollback(transaction);
        return base.TransactionRolledBackAsync(transaction, eventData, cancellationToken);
    }

    public override void TransactionFailed(
        DbTransaction transaction,
        TransactionErrorEventData eventData)
    {
        dispatcher.Rollback(transaction);
        base.TransactionFailed(transaction, eventData);
    }

    public override Task TransactionFailedAsync(
        DbTransaction transaction,
        TransactionErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        dispatcher.Rollback(transaction);
        return base.TransactionFailedAsync(transaction, eventData, cancellationToken);
    }
}
