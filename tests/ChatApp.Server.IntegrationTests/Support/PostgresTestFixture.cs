using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Support;

/// <summary>
/// 提供独立 PostgreSQL 实例：优先 <c>CHATAPP_TEST_POSTGRES</c>，否则启动 Testcontainers。
/// </summary>
public sealed class PostgresTestFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public bool IsAvailable { get; private set; }

    public string? SkipReason { get; private set; }

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var envConnection = Environment.GetEnvironmentVariable("CHATAPP_TEST_POSTGRES");
        if (!string.IsNullOrWhiteSpace(envConnection))
        {
            if (await TryMigrateAsync(envConnection))
            {
                ConnectionString = envConnection;
                IsAvailable = true;
                return;
            }

            SkipReason = "CHATAPP_TEST_POSTGRES is set but connection/migration failed.";
            return;
        }

        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:16.8")
                .Build();

            await _container.StartAsync();

            if (await TryMigrateAsync(_container.GetConnectionString()))
            {
                ConnectionString = _container.GetConnectionString();
                IsAvailable = true;
                return;
            }

            SkipReason = "Testcontainers PostgreSQL started but migration failed.";
        }
        catch (Exception ex)
        {
            SkipReason = $"PostgreSQL unavailable: {ex.Message}";
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    public UserDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<UserDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        return new UserDbContext(options);
    }

    private static async Task<bool> TryMigrateAsync(string connectionString)
    {
        try
        {
            var options = new DbContextOptionsBuilder<UserDbContext>()
                .UseNpgsql(connectionString)
                .Options;

            await using var context = new UserDbContext(options);
            await context.Database.MigrateAsync();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"PostgreSQL migration failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}

[CollectionDefinition(nameof(PostgresCollection))]
public sealed class PostgresCollection : ICollectionFixture<PostgresTestFixture>;
