using Xunit;

namespace ChatApp.Server.IntegrationTests.Support;

[CollectionDefinition(nameof(RedisCollection))]
public sealed class RedisCollection : ICollectionFixture<RedisTestFixture>;
