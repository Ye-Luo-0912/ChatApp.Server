using Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Infrastructure.Services;

/// <summary>
/// API/Worker 共享的 Realtime PostgreSQL 连接池。
/// 每个进程只创建一个 NpgsqlDataSource，避免 Presence、证据和导出读取器各自
/// 建立连接池并在批量扇出时放大连接与握手成本。
/// </summary>
public sealed class RealtimePostgresDataSource : IDisposable
{
    private readonly NpgsqlDataSource? _dataSource;

    public RealtimePostgresDataSource(
        IOptions<MessageEvidenceOptions> evidenceOptions,
        IOptions<DataExportStorageOptions> exportOptions,
        ILogger<RealtimePostgresDataSource> logger)
    {
        var evidence = evidenceOptions.Value;
        var export = exportOptions.Value;
        var connectionString = !string.IsNullOrWhiteSpace(export.RealtimeConnectionString)
            ? export.RealtimeConnectionString
            : evidence.RealtimeConnectionString;

        Schema = string.IsNullOrWhiteSpace(evidence.Schema)
            ? "realtime"
            : evidence.Schema.Trim();

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            logger.LogDebug("未配置 Realtime PostgreSQL 连接串，共享 DataSource 不创建");
            return;
        }

        _dataSource = NpgsqlDataSource.Create(connectionString);
    }

    public string Schema { get; }

    public NpgsqlDataSource? DataSource => _dataSource;

    public void Dispose() => _dataSource?.Dispose();
}
