using System.Data.Common;
using System.Collections.Concurrent;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Infrastructure.Diagnostics;

/// <summary>
/// 记录所有 EF Core 发起的 DbCommand 执行次数，供 /debug/metrics 采样。
/// 注册为单例，所有 DbContextPool 实例共享同一计数器。
/// </summary>
public sealed class DbCommandCounterInterceptor : DbCommandInterceptor
{
    public const string RequestStateItemKey =
        "ChatApp.Server.Diagnostics.DbRequestCommandCounter";

    private long _commandCount;
    private long _authFenceCommandCount;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ConcurrentDictionary<string, long> _byEndpoint = new(StringComparer.Ordinal);

    public DbCommandCounterInterceptor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public long TotalCommandsExecuted => Interlocked.Read(ref _commandCount);

    public long TotalAuthFenceCommands => Interlocked.Read(ref _authFenceCommandCount);

    public void BeginRequest(HttpContext context)
        => context.Items[RequestStateItemKey] = new DbRequestCommandCounter();

    public long GetRequestCount(HttpContext context)
        => context.Items.TryGetValue(RequestStateItemKey, out var value)
           && value is DbRequestCommandCounter state
            ? state.Count
            : 0;

    public long GetAuthFenceRequestCount(HttpContext context)
        => context.Items.TryGetValue(RequestStateItemKey, out var value)
           && value is DbRequestCommandCounter state
            ? state.AuthFenceCount
            : 0;

    public double GetPoolWaitMilliseconds(HttpContext context)
        => context.Items.TryGetValue(RequestStateItemKey, out var value)
           && value is DbRequestCommandCounter state
            ? state.PoolWaitMilliseconds
            : 0;

    public IReadOnlyDictionary<string, long> GetEndpointCounts()
        => _byEndpoint.ToDictionary(static pair => pair.Key, static pair => pair.Value,
            StringComparer.Ordinal);

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        RecordCommand(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        RecordCommand(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        RecordCommand(command);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        RecordCommand(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        RecordCommand(command);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        RecordCommand(command);
        return base.ScalarExecuting(command, eventData, result);
    }

    private void RecordCommand(DbCommand command)
    {
        Interlocked.Increment(ref _commandCount);
        var authFenceCommand = command.CommandText.Contains(
            "auth-fence", StringComparison.OrdinalIgnoreCase);
        if (authFenceCommand)
            Interlocked.Increment(ref _authFenceCommandCount);

        var context = _httpContextAccessor.HttpContext;
        if (context is null)
        {
            _byEndpoint.AddOrUpdate("__background__", 1, static (_, current) => current + 1);
            return;
        }

        if (context.Items.TryGetValue(RequestStateItemKey, out var value)
            && value is DbRequestCommandCounter state)
        {
            // AuthSnapshotStore tags only its authoritative fence projection.
            // This keeps the performance header specific to authentication and
            // does not mistake the endpoint's own profile query for auth work.
            state.Increment(authFenceCommand);
        }

        var endpoint = context.GetEndpoint()?.DisplayName ?? "__unmatched__";
        _byEndpoint.AddOrUpdate(endpoint, 1, static (_, current) => current + 1);
    }
}
