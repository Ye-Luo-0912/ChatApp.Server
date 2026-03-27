using Core.Models.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Models.Config;

public class ApplicationUserConfig : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.Property(u => u.AvatarUrl)
            .HasMaxLength(2048); // 示例: 限制 AvatarUrl 属性的最大长度为 2048 个字符
        
        builder.Property(u => u.Signature)
            .HasMaxLength(500);  // 示例: 限制 Signature (个性签名) 属性的最大长度为 500 个字符

        builder.Property(u => u.Region)
            .HasMaxLength(200);  // 示例: 限制 Region (地区) 属性的最大长度为 200 个字符

       
    }
}