using System.Net;
using Core.Interfaces.Cache;
using Core.Settings;
using Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Quality;

public sealed class GeoLocationServiceTests
{
    [Fact]
    public async Task TransientProviderFailures_AreRetried_NotNegativeCached_AndResponsesDisposed()
    {
        var handler = new SequenceHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new TrackingContent("""{"status":"fail"}"""),
            });
        var cache = new MemoryDerivedCache();
        var service = CreateService(handler, cache);

        Assert.Equal("未知", await service.GetLocationAsync("8.8.8.8"));
        Assert.Equal(3, handler.RequestCount);
        Assert.Empty(cache.Writes);
        Assert.All(handler.Contents, content => Assert.True(content.Disposed));

        // A transient outage must not poison the cache for an hour.
        Assert.Equal("未知", await service.GetLocationAsync("8.8.8.8"));
        Assert.Equal(6, handler.RequestCount);
        Assert.Empty(cache.Writes);
    }

    [Fact]
    public async Task HttpClientTimeout_IsRetried_AndNotNegativeCached()
    {
        var handler = new SequenceHandler(
            _ => throw new TaskCanceledException(
                "simulated HttpClient timeout",
                new TimeoutException("simulated timeout")));
        var cache = new MemoryDerivedCache();
        var service = CreateService(handler, cache);

        Assert.Equal("未知", await service.GetLocationAsync("1.1.1.1"));
        Assert.Equal(3, handler.RequestCount);
        Assert.Empty(cache.Writes);
    }

    private static GeoLocationService CreateService(
        SequenceHandler handler,
        MemoryDerivedCache cache)
        => new(
            new StubHttpClientFactory(handler),
            NullLogger<GeoLocationService>.Instance,
            cache,
            Options.Create(new SecurityOptions { SecretEncryptionKey = "geo-test-secret" }),
            Options.Create(new GeoLocationOptions
            {
                AllowExternalFallback = true,
                MaxLocalEntries = 100,
            }),
            new LocalGeoLocationDatabase(
                Options.Create(new GeoLocationOptions { MaxLocalEntries = 100 }),
                NullLogger<LocalGeoLocationDatabase>.Instance));

    private sealed class StubHttpClientFactory(SequenceHandler handler) : IHttpClientFactory
    {
        private readonly HttpClient _client = new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://geo.example/"),
        };

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class SequenceHandler(
        Func<int, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => Volatile.Read(ref _requestCount);
        public List<TrackingContent> Contents { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = responseFactory(Interlocked.Increment(ref _requestCount));
            if (response.Content is TrackingContent content)
                Contents.Add(content);
            return Task.FromResult(response);
        }
    }

    private sealed class TrackingContent(string value) : HttpContent
    {
        public bool Disposed { get; private set; }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context)
            => stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(value)).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = System.Text.Encoding.UTF8.GetByteCount(value);
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class MemoryDerivedCache : IDerivedCache
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
        public List<TimeSpan> Writes { get; } = [];

        public Task<CacheLookup<T>> TryGetAsync<T>(
            string key,
            CancellationToken cancellationToken = default)
            => Task.FromResult(
                _values.TryGetValue(key, out var value) && value is T typed
                    ? CacheLookup<T>.Hit(typed)
                    : CacheLookup<T>.Miss);

        public Task<IReadOnlyList<CacheLookup<T>>> TryGetManyAsync<T>(
            IReadOnlyList<string> keys,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CacheLookup<T>>>(
                keys.Select(key => TryGetAsync<T>(key).Result).ToArray());

        public Task SetAsync<T>(
            string key,
            T value,
            TimeSpan ttl,
            CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            Writes.Add(ttl);
            return Task.CompletedTask;
        }

        public Task SetManyAsync<T>(
            IReadOnlyList<KeyValuePair<string, T>> values,
            TimeSpan ttl,
            CancellationToken cancellationToken = default)
        {
            foreach (var value in values)
                _values[value.Key] = value.Value;
            if (values.Count > 0)
                Writes.Add(ttl);
            return Task.CompletedTask;
        }

        public Task RemoveManyAsync(
            IReadOnlyList<string> keys,
            CancellationToken cancellationToken = default)
        {
            foreach (var key in keys)
                _values.Remove(key);
            return Task.CompletedTask;
        }
    }
}
