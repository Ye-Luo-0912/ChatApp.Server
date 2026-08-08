using System.Buffers;
using System.Text.Json;

namespace Infrastructure.Serialization;

/// <summary>
/// Writes one JSON object directly to a stream and can splice prevalidated JSON
/// values from files without creating a contiguous in-memory buffer.
/// </summary>
internal sealed class SequentialJsonObjectWriter(Stream destination)
{
    private const int StackPropertyBufferBytes = 256;
    private const string Hex = "0123456789ABCDEF";

    private static ReadOnlySpan<byte> ObjectStart => "{"u8;
    private static ReadOnlySpan<byte> ObjectEnd => "}"u8;
    private static ReadOnlySpan<byte> Separator => ","u8;
    private static ReadOnlySpan<byte> NameSeparator => ":"u8;

    private bool _started;
    private bool _completed;
    private bool _hasProperty;

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
            throw new InvalidOperationException("JSON object has already started.");

        cancellationToken.ThrowIfCancellationRequested();
        _started = true;
        destination.Write(ObjectStart);
        return ValueTask.CompletedTask;
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
        CancellationToken cancellationToken = default,
        bool deleteSourceAfterCopy = false)
    {
        await WritePropertyPrefixAsync(propertyName, cancellationToken).ConfigureAwait(false);
        try
        {
            await using var source = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(destination, 64 * 1024, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (deleteSourceAfterCopy)
            {
                try { File.Delete(path); }
                catch { /* the caller's staging cleanup remains the fallback */ }
            }
        }
    }

    public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        cancellationToken.ThrowIfCancellationRequested();
        _completed = true;
        destination.Write(ObjectEnd);
        return new ValueTask(destination.FlushAsync(cancellationToken));
    }

    private ValueTask WritePropertyPrefixAsync(
        string propertyName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        EnsureWritable();
        cancellationToken.ThrowIfCancellationRequested();

        if (_hasProperty)
            destination.Write(Separator);

        var maxBytes = checked(propertyName.Length * 6 + 2);
        if (maxBytes <= StackPropertyBufferBytes)
        {
            Span<byte> encodedName = stackalloc byte[maxBytes];
            var written = EncodeJsonString(propertyName, encodedName);
            destination.Write(encodedName[..written]);
        }
        else
        {
            var encodedName = ArrayPool<byte>.Shared.Rent(maxBytes);
            try
            {
                var written = EncodeJsonString(propertyName, encodedName);
                destination.Write(encodedName.AsSpan(0, written));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(encodedName);
            }
        }

        destination.Write(NameSeparator);
        _hasProperty = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Encodes a JSON string directly into caller-owned storage. Property names
    /// in this writer are normally short constants, so the common path uses a
    /// stack buffer and performs no managed allocation. The pointer loop also
    /// avoids the temporary byte[] created by SerializeToUtf8Bytes.
    /// </summary>
    private static unsafe int EncodeJsonString(string value, Span<byte> destination)
    {
        fixed (char* source = value)
        fixed (byte* target = destination)
        {
            var current = source;
            var end = source + value.Length;
            var output = target;
            *output++ = (byte)'"';

            while (current < end)
            {
                var character = *current++;
                switch (character)
                {
                    case '"':
                    case '\\':
                        *output++ = (byte)'\\';
                        *output++ = (byte)character;
                        continue;
                    case '\b':
                        *output++ = (byte)'\\';
                        *output++ = (byte)'b';
                        continue;
                    case '\f':
                        *output++ = (byte)'\\';
                        *output++ = (byte)'f';
                        continue;
                    case '\n':
                        *output++ = (byte)'\\';
                        *output++ = (byte)'n';
                        continue;
                    case '\r':
                        *output++ = (byte)'\\';
                        *output++ = (byte)'r';
                        continue;
                    case '\t':
                        *output++ = (byte)'\\';
                        *output++ = (byte)'t';
                        continue;
                }

                if (character < 0x20)
                {
                    *output++ = (byte)'\\';
                    *output++ = (byte)'u';
                    *output++ = (byte)'0';
                    *output++ = (byte)'0';
                    *output++ = (byte)Hex[character >> 4];
                    *output++ = (byte)Hex[character & 0x0F];
                    continue;
                }

                uint scalar = character;
                if (character >= '\uD800' && character <= '\uDBFF'
                    && current < end
                    && *current >= '\uDC00' && *current <= '\uDFFF')
                {
                    scalar = (uint)(0x10000
                                    + ((character - 0xD800) << 10)
                                    + (*current++ - 0xDC00));
                }
                else if (character >= '\uD800' && character <= '\uDFFF')
                {
                    // Match UTF-8 replacement behavior for an unpaired UTF-16
                    // surrogate while keeping the operation allocation-free.
                    scalar = 0xFFFD;
                }

                if (scalar <= 0x7F)
                {
                    *output++ = (byte)scalar;
                }
                else if (scalar <= 0x7FF)
                {
                    *output++ = (byte)(0xC0 | (scalar >> 6));
                    *output++ = (byte)(0x80 | (scalar & 0x3F));
                }
                else if (scalar <= 0xFFFF)
                {
                    *output++ = (byte)(0xE0 | (scalar >> 12));
                    *output++ = (byte)(0x80 | ((scalar >> 6) & 0x3F));
                    *output++ = (byte)(0x80 | (scalar & 0x3F));
                }
                else
                {
                    *output++ = (byte)(0xF0 | (scalar >> 18));
                    *output++ = (byte)(0x80 | ((scalar >> 12) & 0x3F));
                    *output++ = (byte)(0x80 | ((scalar >> 6) & 0x3F));
                    *output++ = (byte)(0x80 | (scalar & 0x3F));
                }
            }

            *output++ = (byte)'"';
            return (int)(output - target);
        }
    }

    private void EnsureWritable()
    {
        if (!_started || _completed)
            throw new InvalidOperationException("JSON object is not writable.");
    }
}
