using FlashSale.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlashSale.Api.Data.Configurations;

public class OrderConfiguration :
    IEntityTypeConfiguration<Order>
{
    public void Configure(
        EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.UserId)
            .IsRequired();

        builder.Property(x => x.ProductId)
            .IsRequired();

        builder.Property(x => x.Quantity)
            .IsRequired();

        builder.Property(x => x.Status)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .IsRequired();

        // Stage 2 之後需要大量以 ProductId 統計訂單數量。
        builder.HasIndex(x => x.ProductId);

        builder.Property(x => x.IdempotencyKey)
            .HasMaxLength(128);

        // Stage 6：篩選唯一索引 —— 同一個 IdempotencyKey 只能有一筆訂單。
        //
        // 必須加 HasFilter 排除 NULL。SQL Server 的唯一索引把多個 NULL
        // 視為互相衝突，不加篩選的話「沒帶 Key 的訂單」只能存在一筆。
        //
        // 這是重複訂單的最後一道防線，由資料庫強制執行。
        builder.HasIndex(x => x.IdempotencyKey)
            .IsUnique()
            .HasFilter("[IdempotencyKey] IS NOT NULL");
    }
}
