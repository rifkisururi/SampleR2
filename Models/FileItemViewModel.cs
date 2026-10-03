namespace SampleR2.Models
{
    /// <summary>
    /// Menampilkan informasi ringkas satu file yang sudah terupload.
    /// </summary>
    public class FileItemViewModel
    {
        public string Name { get; set; } = string.Empty;

        public long Size { get; set; }

        public DateTime LastModified { get; set; }

        /// <summary>
        /// URL publik file jika tersedia (wwwroot selalu ada, R2 hanya jika PublicBaseUrl diisi).
        /// </summary>
        public string? Url { get; set; }

        public string SizeText => FormatSize(Size);

        public static string FormatSize(long bytes) => bytes switch
        {
            >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F2} GB",
            >= 1_048_576 => $"{bytes / 1_048_576.0:F2} MB",
            >= 1024 => $"{bytes / 1024.0:F2} KB",
            _ => $"{bytes} B",
        };
    }
}
