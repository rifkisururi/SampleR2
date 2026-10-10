using System.Text.RegularExpressions;

namespace SampleR2.Services;

/// <summary>
/// Pembuat nama file unik yang aman: mencegah path traversal dan tabrakan nama file.
/// Hasil: {guid}-{nama-asli-yang-diamankan}{ekstensi}.
/// </summary>
public static class FileNamer
{
    public static string BuildUniqueFileName(string originalFileName)
    {
        var fileName = Path.GetFileName(originalFileName); // buang bagian path
        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName).ToLowerInvariant();

        var safeName = Regex.Replace(name, @"[^A-Za-z0-9_-]", "-").Trim('-');
        if (safeName.Length > 40)
        {
            safeName = safeName[..40];
        }

        return $"{Guid.NewGuid():N}{(safeName.Length > 0 ? $"-{safeName}" : string.Empty)}{ext}";
    }
}
