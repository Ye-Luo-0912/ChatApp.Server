using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Extensions;

public static class ModelBuilderExtensions
{
    public static void HasNoForeignKeyConstraints(this EntityTypeBuilder builder)
    {
        // 关闭所有外键约束生成
        builder.Metadata.GetForeignKeys()
            .ToList()
            .ForEach(fk => fk.IsOwnership = false);

        // 设置全局无约束模式
        foreach (var relationship in builder.Metadata.GetForeignKeys())
        {
            relationship.DeleteBehavior = DeleteBehavior.ClientNoAction;
            relationship.IsRequired = false;
            relationship.IsOwnership = false;
        }
    }
}