using Microsoft.EntityFrameworkCore;
using SampleR2.Models;

namespace SampleR2.Data;

/// <summary>
/// DbContext PostgreSQL (Neon). Skema dibuat otomatis via EnsureCreated() di Program.cs;
/// untuk aplikasi produksi gunakan EF migrations.
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ProdukLocalStorage> ProdukLocal => Set<ProdukLocalStorage>();

    public DbSet<ProdukR2> ProdukR2 => Set<ProdukR2>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProdukLocalStorage>(e =>
        {
            e.ToTable("produk_local");
            e.Property(x => x.NamaProduk).HasMaxLength(100).IsRequired();
            e.Property(x => x.Harga).HasPrecision(18, 2);
            e.Property(x => x.FileName).HasMaxLength(260).IsRequired();
            e.Property(x => x.ImageUrl).HasMaxLength(500).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(100);
        });

        modelBuilder.Entity<ProdukR2>(e =>
        {
            e.ToTable("produk_r2");
            e.Property(x => x.NamaProduk).HasMaxLength(100).IsRequired();
            e.Property(x => x.Harga).HasPrecision(18, 2);
            e.Property(x => x.ObjectKey).HasMaxLength(500).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(100);
        });
    }
}
