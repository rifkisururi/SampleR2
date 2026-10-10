namespace SampleR2.Services;

/// <summary>
/// Whitelist tipe gambar yang diizinkan untuk upload, dipetakan dari ekstensi file.
/// Content type selalu ditentukan server (bukan diambil dari browser).
/// </summary>
public static class ImageTypes
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".avif"] = "image/avif",
    };

    /// <summary>Content type resmi dari ekstensi file, atau null jika ekstensi tidak diizinkan.</summary>
    public static string? ResolveContentType(string? fileName)
    {
        var ext = Path.GetExtension(Path.GetFileName(fileName ?? string.Empty)).ToLowerInvariant();
        return ByExtension.TryGetValue(ext, out var contentType) ? contentType : null;
    }

    /// <summary>Daftar ekstensi yang diizinkan (untuk pesan error / atribut accept).</summary>
    public static IEnumerable<string> Extensions => ByExtension.Keys;
}
