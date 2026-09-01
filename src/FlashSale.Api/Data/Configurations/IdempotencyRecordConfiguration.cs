using FlashSale.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlashSale.Api.Data.Configurations;

public class IdempotencyRecordConfiguration :
    IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("IdempotencyRecords");

        // Key 本身就是主鍵 —— 併發 INSERT 時由主鍵衝突擋掉第二個。
        builder.HasKey(x => x.Key);

        builder.Property(x => x.Key)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(x => x.Status)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(x => x.ResponseBody)
            .HasMaxLength(4000);

        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.ExpiresAt).IsRequired();

        // 清理過期記錄時要靠這個索引，否則會全表掃描。
        builder.HasIndex(x => x.ExpiresAt);
    }
}
