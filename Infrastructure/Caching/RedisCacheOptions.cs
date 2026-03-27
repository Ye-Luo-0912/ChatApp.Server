namespace Infrastructure.Caching;

public class RedisCacheOptions
{
    public string KeyPrefix { get; set; } = "cache:";
    public TimeSpan DefaultSlidingExpiration { get; set; } = TimeSpan.FromMinutes(30);
    public double ExpirationJitterPercent { get; set; } = 0.05;
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan DefaultLockExpiry { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan NullValueExpiration { get; set; } = TimeSpan.FromMinutes(5);
}