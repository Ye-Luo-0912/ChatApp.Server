using Core.Models.Auth;
using Core.Models.Export;

namespace Core.Interfaces;

/// <summary>账号数据导出的应用边界。</summary>
public interface IDataExportService
{
    Task<(AuthOperationResult Result, string? JobId)> EnqueueAsync(
        long userId,
        string? password,
        string? mfaCode,
        string? stepUpToken,
        CancellationToken cancellationToken = default);

    Task<DataExportStatusDto?> GetStatusAsync(
        long userId,
        string jobId,
        CancellationToken cancellationToken = default);

    Task<AuthOperationResult> CancelAsync(
        long userId,
        string jobId,
        CancellationToken cancellationToken = default);

    Task<(Stream? Stream, string? FileName, string? Error)> OpenDownloadAsync(
        long userId,
        string jobId,
        CancellationToken cancellationToken = default);

    Task DeleteAllForUserAsync(
        long userId,
        CancellationToken cancellationToken = default);
}
