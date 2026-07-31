using Microsoft.Extensions.Caching.Memory;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using ChatApp.Realtime.Integration;
using Core.Interfaces;
using Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Infrastructure.Services;

/// <summary>
/// 从 Realtime 消息表（优先）或 NATS 总线获取证据；带超时、短时缓存与简易熔断。
/// </summary>
public sealed class RealtimeMessageEvidenceProvider : IMessageEvidenceProvider
{
    private static readonly Meter Meter = new("Infrastructure.Moderation.Evidence");
    private readonly Counter<long> _hits;
    private readonly Counter<long> _misses;
    private readonly Counter<long> _failures;
    private readonly Histogram<double> _durationMs;

    private readonly MessageEvidenceOptions _options;
    private readonly IRealtimeMessageBus? _bus;
    private readonly ILogger<RealtimeMessageEvidenceProvider> _logger;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions
    {
        SizeLimit = 4096,
        CompactionPercentage = 0.25,
    });

    private int _consecutiveFailures;
    private long _circuitOpenUntilUnix;

    public RealtimeMessageEvidenceProvider(
        IOptions<MessageEvidenceOptions> options,
        ILogger<RealtimeMessageEvidenceProvider> logger,
        IRealtimeMessageBus? bus = null)
    {
        _options = options.Value;
        _logger = logger;
        _bus = bus;
        _hits = Meter.CreateCounter<long>("moderation.evidence.hits");
        _misses = Meter.CreateCounter<long>("moderation.evidence.misses");
        _failures = Meter.CreateCounter<long>("moderation.evidence.failures");
        _durationMs = Meter.CreateHistogram<double>("moderation.evidence.duration", "ms");
    }

    public async Task<MessageEvidenceSnapshot?> TryGetAsync(
        string messageId,
        long? requestingUserId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            return null;

        var key = messageId.Trim();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (_cache.TryGetValue<MessageEvidenceSnapshot>(key, out var cached))
        {
            _hits.Add(1);
            return cached;
        }

        if (Volatile.Read(ref _circuitOpenUntilUnix) > now)
        {
            _failures.Add(1);
            _logger.LogWarning("消息证据熔断开启中，跳过查询 messageId={MessageId}", key);
            return null;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Math.Max(200, _options.TimeoutMilliseconds));

            MessageEvidenceSnapshot? snapshot = null;
            if (!string.IsNullOrWhiteSpace(_options.RealtimeConnectionString))
                snapshot = await LoadFromPostgresAsync(key, timeout.Token).ConfigureAwait(false);
            else if (_bus is not null && requestingUserId is > 0)
                snapshot = await LoadFromBusAsync(requestingUserId.Value, key, timeout.Token).ConfigureAwait(false);

            if (snapshot is null)
                _misses.Add(1);
            else
            {
                _hits.Add(1);
                var ttl = Math.Max(1, _options.CacheSeconds);
                _cache.Set(key, snapshot, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(ttl),
                    Size = 1,
                });
            }

            Interlocked.Exchange(ref _consecutiveFailures, 0);
            return snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _failures.Add(1);
            var failures = Interlocked.Increment(ref _consecutiveFailures);
            var threshold = Math.Max(1, _options.CircuitBreakerFailureThreshold);
            if (failures >= threshold)
            {
                var openFor = Math.Max(5, _options.CircuitBreakerDurationSeconds);
                Interlocked.Exchange(ref _circuitOpenUntilUnix, now + openFor);
                _logger.LogError(ex, "消息证据连续失败 {Failures} 次，熔断 {Seconds}s", failures, openFor);
            }
            else
            {
                _logger.LogWarning(ex, "消息证据查询失败 messageId={MessageId}", key);
            }

            return null;
        }
        finally
        {
            _durationMs.Record(sw.Elapsed.TotalMilliseconds);
        }
    }

    private async Task<MessageEvidenceSnapshot?> LoadFromPostgresAsync(
        string messageId, CancellationToken cancellationToken)
    {
        var schema = string.IsNullOrWhiteSpace(_options.Schema) ? "realtime" : _options.Schema.Trim();
        await using var conn = new NpgsqlConnection(_options.RealtimeConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"""
             SELECT message_id, sender_user_id, receiver_user_id, content, received_at_ms,
                    edit_version, edited_at_ms, recalled_at_ms
             FROM "{schema}"."messages"
             WHERE message_id = @id
             LIMIT 1
             """,
            conn);
        cmd.Parameters.AddWithValue("id", messageId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var rowMessageId = reader.GetString(0);
        var sender = reader.GetInt64(1);
        var receiver = reader.GetInt64(2);
        var rawBody = reader.GetString(3);
        var receivedAtMs = reader.GetInt64(4);
        var editVersion = reader.IsDBNull(5) ? 1 : reader.GetInt32(5);
        long? editedAtMs = reader.IsDBNull(6) ? null : reader.GetInt64(6);
        long? recalledAtMs = reader.IsDBNull(7) ? null : reader.GetInt64(7);
        var body = recalledAtMs is > 0 ? string.Empty : rawBody;
        return ToSnapshot(
            rowMessageId,
            sender,
            receiver,
            receivedAtMs,
            body,
            editVersion <= 0 ? 1 : editVersion,
            editedAtMs,
            recalledAtMs);
    }

    private async Task<MessageEvidenceSnapshot?> LoadFromBusAsync(
        long userId, string messageId, CancellationToken cancellationToken)
    {
        var msg = await _bus!.TryGetMessageByIdAsync(userId, messageId, cancellationToken)
            .ConfigureAwait(false);
        if (msg is null) return null;
        var recalled = msg.RecalledAtMs is > 0 ? msg.RecalledAtMs : null;
        return ToSnapshot(
            msg.MessageId,
            msg.SenderUserId,
            msg.ReceiverUserId,
            msg.ReceivedAtMs,
            recalled is not null ? string.Empty : msg.Content,
            msg.EditVersion <= 0 ? 1 : msg.EditVersion,
            msg.EditedAtMs,
            recalled);
    }

    private static MessageEvidenceSnapshot ToSnapshot(
        string messageId,
        long sender,
        long receiver,
        long receivedAtMs,
        string body,
        int editVersion = 1,
        long? editedAtMs = null,
        long? recalledAtMs = null)
    {
        var stubBody = recalledAtMs is > 0 ? string.Empty : body;
        return new(
            messageId,
            sender,
            receiver,
            DateTimeOffset.FromUnixTimeMilliseconds(receivedAtMs),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stubBody))).ToLowerInvariant(),
            stubBody,
            editVersion,
            editedAtMs,
            recalledAtMs);
    }
}
