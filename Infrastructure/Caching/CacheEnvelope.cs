namespace Infrastructure.Caching
{
    public abstract class CacheEnvelope<T>
    {
        public T? Value { get; set; }

        /// <summary>
        /// 绝对过期时间，表示缓存项的过期时间点。当当前时间超过该时间点时，缓存项将被视为过期并被移除。
        /// </summary>
        public DateTimeOffset? AbsoluteExpiration { get; set; }

        public TimeSpan? SlidingExpiration { get; set; }

        public bool IsNullValue { get; set; }
    }
}
