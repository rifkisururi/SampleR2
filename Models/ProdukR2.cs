using System.ComponentModel.DataAnnotations.Schema;

namespace SampleR2.Models;

/// <summary>
/// Produk dengan gambar yang disimpan di Cloudflare R2 (upload langsung dari browser via presigned URL).
/// </summary>
public class ProdukR2
{
    public int Id { get; set; }

    public required string NamaProduk { get; set; }

    public decimal Harga { get; set; }

    /// <summary>Key object di bucket R2 (format uploads/yyyy/MM/dd/{guid}-{nama}.{ext}).</summary>
    public required string ObjectKey { get; set; }

    public long UkuranByte { get; set; }

    public string? ContentType { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// URL gambar untuk ditampilkan; dihitung di controller saat render
    /// (public URL bila ada, jika tidak presigned GET berumur pendek). Tidak disimpan ke database.
    /// </summary>
    [NotMapped]
    public string? ImageUrl { get; set; }
}
