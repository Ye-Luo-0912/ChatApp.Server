using System.Text.Json;

namespace Infrastructure.Serialization;

/// <summary>
/// Writes one JSON object directly to a stream and can splice prevalidated JSON
/// values from files without creating a contiguous in-memory buffer.
/// </summary>
internal sealed class SequentialJsonObjectWriter(Stream destination)
{
    private static readonly byte[] ObjectStart = "{"u8.ToArray();
    private static readonly byte[] ObjectEnd = "}"u8.ToArray();
    private static readonly byte[] Separator = ","u8.ToArray();
    private static readonly byte[] NameSeparator = ":"u8.ToArray();

    private bool _started;
    private bool _completed;
    private bool _hasProperty;

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
            throw new InvalidOperationException("JSON object has already started.");

        _started = true;
        await destination.WriteAsync(ObjectStart, cancellationToken).ConfigureAwait(false);
    }

    public async Task WritePropertyAsync<T>(
        string propertyName,
        T value,
        CancellationToken cancellationToken = default)
    {
        await WritePropertyPrefixAsync(propertyName, cancellationToken).ConfigureAwait(false);
        await JsonSerializer.SerializeAsync(destination, value, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task WriteRawJsonFilePropertyAsync(
        string propertyName,
        string path,
        CancellationToken cancellationToken = default)
    {
        await WritePropertyPrefixAsync(propertyName, cancellationToken).ConfigureAwait(false);
        await using var source = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, 64 * 1024, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        _completed = true;
        await destination.WriteAsync(ObjectEnd, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WritePropertyPrefixAsync(
        string propertyName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        EnsureWritable();

        if (_hasProperty)
            await destination.WriteAsync(Separator, cancellationToken).ConfigureAwait(false);

        var encodedName = JsonSerializer.SerializeToUtf8Bytes(propertyName);
        await destination.WriteAsync(encodedName, cancellationToken).ConfigureAwait(false);
        await destination.WriteAsync(NameSeparator, cancellationToken).ConfigureAwait(false);
        _hasProperty = true;
    }

    private void EnsureWritable()
    {
        if (!_started || _completed)
            throw new InvalidOperationException("JSON object is not writable.");
    }
}
