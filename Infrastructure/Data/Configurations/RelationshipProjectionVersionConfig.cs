using Core.Models.Friend;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Data.Configurations;

public sealed class RelationshipProjectionVersionConfig
    : IEntityTypeConfiguration<RelationshipProjectionVersion>
{
    public void Configure(EntityTypeBuilder<RelationshipProjectionVersion> builder)
    {
        builder.ToTable("T_RelationshipProjectionVersion");
        builder.HasKey(item => new { item.OwnerUserId, item.ListType });
        builder.Property(item => item.ListType).HasConversion<short>();
        builder.Property(item => item.Version).IsRequired();
        builder.Property(item => item.UpdatedAtMs).IsRequired();
        builder.HasIndex(item => item.UpdatedAtMs)
            .HasDatabaseName("IX_RelationshipProjectionVersion_UpdatedAtMs");
    }
}
