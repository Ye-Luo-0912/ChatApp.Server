using System.Text.Json;
using ChatApp.Server.ConfigServiceLibrary.Data;
using ChatApp.Server.ConfigServiceLibrary.Models;
using Microsoft.EntityFrameworkCore;
using SQLitePCL;

namespace ChatApp.Server.ConfigServiceLibrary.Services
{
    /// <summary>
    /// 配置服务，管理应用程序设置
    /// </summary>
    public class ConfigService : IDisposable
    {
        //
        private static readonly Lazy<ConfigService> _instance = new(() => new ConfigService());
        private readonly ConfigDbContext _context;
        private bool _disposed;

        // 静态构造函数，确保 SQLite 提供程序已初始化
        static ConfigService()
        {
            Batteries.Init();
        }
        private ConfigService()
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            var connectionString = configuration.GetConnectionString("DefaultConnection");
            var options = new DbContextOptionsBuilder<ConfigDbContext>()
                .UseSqlite(connectionString)
                .Options;

            _context = new ConfigDbContext(options);
        }


        public static ConfigService Instance => _instance.Value;

        public async Task<List<ConfigItem>> GetConfigItemsAsync()
        {
            return await _context.ConfigItem.ToListAsync();
        }

        public async Task<ConfigItem?> GetConfigItemAsync(string key)
        {
            ArgumentNullException.ThrowIfNull(key);
            return await _context.ConfigItem.SingleOrDefaultAsync(item => item.Key == key);
        }

        public async Task AddConfigItemAsync(ConfigItem configItem)
        {
            _context.ConfigItem.Add(configItem);
            await _context.SaveChangesAsync();
        }

        public async Task UpdateConfigItemAsync(ConfigItem configItem)
        {
            _context.ConfigItem.Update(configItem);
            await _context.SaveChangesAsync();
        }

        public async Task DeleteConfigItemAsync(string key)
        {
            var configItem = await GetConfigItemAsync(key);
            if (configItem != null)
            {
                _context.ConfigItem.Remove(configItem);
                await _context.SaveChangesAsync();
            }
        }

        /// <summary>
        /// 设置配置项
        /// </summary>
        public async Task SetConfigItemAsync<T>(string key, T value)
        {
            var jsonString = JsonSerializer.Serialize(value);
            var configItem = new ConfigItem(key, jsonString);
            await AddConfigItemAsync(configItem);
        }

        /// <summary>
        /// 获取配置项
        /// </summary>
        public async Task<T?> GetConfigItemAsync<T>(string key)
        {
            var configItem = await GetConfigItemAsync(key);
            if (configItem?.Value == null) return default;
            return JsonSerializer.Deserialize<T>(configItem.Value);
        }

        public async Task<string?> GetConfigValueAsync(string key)
        {
            var configItem = await _context.ConfigItem.SingleOrDefaultAsync(item => item.Key == key);
            return configItem?.Value;
        }

        
        public void Dispose()
        {
            if (_disposed) 
                return;
            _context.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
