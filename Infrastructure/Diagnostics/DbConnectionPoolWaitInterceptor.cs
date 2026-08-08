using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Infrastructure.Diagnostics;

/// <summary>
/// Measures the time spent opening/checking out an Npgsql connection.  The
/// value is a diagnostic signal, not a retry mechanism; it is attached to the
/// current request when one exists and remains available as a process total
/// for worker measurements.
/// </summary>
public sealed class DbConnectionPoolWaitInterceptor : DbConnectionInterceptor
{
    public const string RequestStateItemKey =
        "ChatApp.Server.Diagnostics.DbRequestCommandCounter";

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ConcurrentDictionary<DbConnection, long> _opening = new();
    private long _totalWaitTicks;

    public DbConnectionPoolWaitInterceptor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public double TotalWaitMilliseconds
        => Interlocked.Read(ref _totalWaitTicks) * 1000d / Stopwatch.Frequency;

    public override InterceptionResult ConnectionOpening(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result)
    {
        _opening.TryAdd(connection, Stopwatch.GetTimestamp());
        return base.ConnectionOpening(connection, eventData, result);
    }

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        _opening.TryAdd(connection, Stopwatch.GetTimestamp());
        return base.ConnectionOpeningAsync(connection, eventData, result, cancellationToken);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        Record(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Record(connection);
        return base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    private void Record(DbConnection connection)
    {
        if (!_opening.TryRemove(connection, out var started))
            return;

        var elapsedTicks = Math.Max(0, Stopwatch.GetTimestamp() - started);
        Interlocked.Add(ref _totalWaitTicks, elapsedTicks);

        var context = _httpContextAccessor.HttpContext;
        if (context?.Items[DbCommandCounterInterceptor.RequestStateItemKey]
            is DbRequestCommandCounter state)
        {
            state.AddPoolWait(Stopwatch.GetElapsedTime(started, Stopwatch.GetTimestamp()).TotalMilliseconds);
        }
    }
}
