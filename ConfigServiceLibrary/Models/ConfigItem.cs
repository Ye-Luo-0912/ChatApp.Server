namespace ChatApp.Server.ConfigServiceLibrary.Models;

/// <summary>
/// 配置项实体类
/// </summary>
/// <param name="Key">配置键</param>
/// <param name="Value">配置值</param>
public record ConfigItem(string Key, string? Value)
{
    /// <summary>
    /// 主键ID
    /// </summary>
    public int Id {get; set;}
}