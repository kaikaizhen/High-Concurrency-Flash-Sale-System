using FlashSale.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlashSale.Api.Data.Configurations;

public class ProductConfiguration :
    IEntityTypeConfiguration<Product>
{
    public void Configure(
        EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.Price)
            .HasColumnType("decimal(18,2)")
            .IsRequired();

        builder.Property(x => x.Stock)
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .IsRequired();

        // Stage 3 Version B：Optimistic Concurrency。
        // IsRowVersion() 同時做兩件事：欄位型別為 SQL Server rowversion，
        // 且成為 Concurrency Token —— 之後所有經由變更追蹤送出的 UPDATE
        // 都會自動附帶 WHERE RowVersion = @original。
        builder.Property(x => x.RowVersion)
            .IsRowVersion();
    }
}
