namespace SampleR2.Models;

/// <summary>
/// Produk dengan gambar yang disimpan di local storage (wwwroot/uploads/produk).
/// </summary>
public class ProdukLocalStorage
{
    public int Id { get; set; }

    public required string NamaProduk { get; set; }

    public decimal Harga { get; set; }

    /// <summary>Nama file di folder wwwroot/uploads/produk.</summary>
    public required string FileName { get; set; }

    /// <summary>URL relatif gambar (dilayani sebagai static file dari wwwroot).</summary>
    public required string ImageUrl { get; set; }

    public long UkuranByte { get; set; }

    public string? ContentType { get; set; }

    public DateTime CreatedAt { get; set; }
}
