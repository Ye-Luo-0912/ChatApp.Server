using Xunit;

namespace ChatApp.Server.IntegrationTests.Support;

[CollectionDefinition(nameof(RedisPostgresCollection))]
public sealed class RedisPostgresCollection :
    ICollectionFixture<RedisTestFixture>,
    ICollectionFixture<PostgresTestFixture>;
