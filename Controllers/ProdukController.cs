using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SampleR2.Data;
using SampleR2.Models;
using SampleR2.Services;
using System.Text.RegularExpressions;

namespace SampleR2.Controllers
{
    /// <summary>
    /// Skenario tabel produk: input produk + gambar, datanya disimpan ke PostgreSQL (Neon).
    /// Gambar bisa disimpan ke local storage wwwroot (tabel produk_local) atau langsung ke
    /// Cloudflare R2 dari browser (tabel produk_r2, via presigned URL).
    /// </summary>
    public class ProdukController : Controller
    {
        private const string ProdukFolder = "uploads/produk"; // di bawah wwwroot
        private const int MaxImageBytes = 50 * 1024 * 1024;   // 50 MB
        private const int PresignedViewMinutes = 30;

        // Key R2 yang sah hanya hasil PresignDirect (uploads/yyyy/MM/dd/{guid}-{nama}.{ext});
        // mencegah FE mengirim key sembarangan untuk direkam ke database.
        private static readonly Regex ValidR2KeyPattern = new(
            @"^uploads/\d{4}/\d{2}/\d{2}/[0-9a-f]{32}(-[A-Za-z0-9_-]{1,40})?\.(jpg|jpeg|png|gif|webp|avif)$",
            RegexOptions.Compiled);

        private readonly AppDbContext _db;
        private readonly IWebHostEnvironment _env;
        private readonly R2StorageService _r2;
        private readonly ILogger<ProdukController> _logger;

        public ProdukController(AppDbContext db, IWebHostEnvironment env, R2StorageService r2, ILogger<ProdukController> logger)
        {
            _db = db;
            _env = env;
            _r2 = r2;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> Index(string? saved)
        {
            await LoadListsAsync();
            ViewBag.Saved = saved;
            ViewBag.R2Configured = _r2.IsConfigured();
            return View();
        }

        // ==================== Simpan ke local storage → tabel produk_local ====================

        [HttpPost]
        [ValidateAntiForgeryToken]
        // Catatan: tanpa [RequestSizeLimit] — Kestrel global (5 GiB) yang menahan body;
        // validasi ukuran dilakukan di bawah agar error tampil rapi, bukan koneksi diputus.
        public async Task<IActionResult> Local(string namaProduk, decimal harga, IFormFile gambar)
        {
            if (string.IsNullOrWhiteSpace(namaProduk) || namaProduk.Trim().Length > 100)
            {
                ModelState.AddModelError(nameof(namaProduk), "Nama produk wajib diisi (maks. 100 karakter).");
            }
            if (harga < 0)
            {
                ModelState.AddModelError(nameof(harga), "Harga tidak boleh negatif.");
            }
            if (gambar is null || gambar.Length == 0)
            {
                ModelState.AddModelError(string.Empty, "Silakan pilih gambar produk.");
            }
            else if (gambar.Length > MaxImageBytes)
            {
                ModelState.AddModelError(string.Empty, $"Ukuran gambar maksimal {FileItemViewModel.FormatSize(MaxImageBytes)}.");
            }
            else if (ImageTypes.ResolveContentType(gambar.FileName) is null)
            {
                ModelState.AddModelError(string.Empty, $"Ekstensi gambar tidak diizinkan ({string.Join(", ", ImageTypes.Extensions)}).");
            }

            if (ModelState.IsValid)
            {
                try
                {
                    var fileName = FileNamer.BuildUniqueFileName(gambar!.FileName);
                    var folder = Path.Combine(_env.WebRootPath ?? "wwwroot", ProdukFolder);
                    Directory.CreateDirectory(folder);

                    await using (var stream = new FileStream(Path.Combine(folder, fileName), FileMode.Create))
                    {
                        await gambar.CopyToAsync(stream);
                    }

                    _db.ProdukLocal.Add(new ProdukLocalStorage
                    {
                        NamaProduk = namaProduk!.Trim(),
                        Harga = harga,
                        FileName = fileName,
                        ImageUrl = $"/{ProdukFolder}/{fileName}",
                        UkuranByte = gambar.Length,
                        ContentType = gambar.ContentType,
                        CreatedAt = DateTime.UtcNow,
                    });
                    await _db.SaveChangesAsync();

                    _logger.LogInformation("Produk {Nama} tersimpan (gambar local: {FileName})", namaProduk, fileName);
                    return RedirectToAction(nameof(Index), new { saved = "local" });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Gagal menyimpan produk (local storage)");
                    ModelState.AddModelError(string.Empty, $"Gagal menyimpan produk: {ex.Message}");
                }
            }

            await LoadListsAsync();
            ViewBag.R2Configured = _r2.IsConfigured();
            return View("Index");
        }

        // ==================== Simpan ke R2 → tabel produk_r2 ====================

        /// <summary>
        /// Dipanggil browser SETELAH upload gambar ke R2 sukses (PUT presigned langsung).
        /// Server memverifikasi gambar benar-benar ada di bucket sebelum merekam produk.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> R2([FromBody] ProdukR2Input input)
        {
            if (input is null || string.IsNullOrWhiteSpace(input.NamaProduk) || input.NamaProduk.Trim().Length > 100)
            {
                return BadRequest(new { error = "Nama produk wajib diisi (maks. 100 karakter)." });
            }
            if (input.Harga < 0)
            {
                return BadRequest(new { error = "Harga tidak boleh negatif." });
            }
            if (input.ObjectKey is null || !ValidR2KeyPattern.IsMatch(input.ObjectKey))
            {
                return BadRequest(new { error = "Key gambar tidak valid." });
            }

            try
            {
                string contentType;
                long ukuran;
                using (var client = _r2.CreateClient())
                {
                    GetObjectMetadataResponse meta;
                    try
                    {
                        // Verifikasi gambar benar-benar terupload; ukuran diambil dari bucket, bukan dari FE.
                        meta = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
                        {
                            BucketName = _r2.GetSetting("BucketName"),
                            Key = input.ObjectKey,
                        });
                    }
                    catch (AmazonS3Exception)
                    {
                        return BadRequest(new { error = "Gambar belum ditemukan di R2 — upload belum selesai." });
                    }

                    ukuran = meta.ContentLength is > 0 ? (long)meta.ContentLength : input.UkuranByte;
                    contentType = meta.Headers.ContentType ?? input.ContentType ?? string.Empty;
                }

                _db.ProdukR2.Add(new ProdukR2
                {
                    NamaProduk = input.NamaProduk.Trim(),
                    Harga = input.Harga,
                    ObjectKey = input.ObjectKey,
                    UkuranByte = ukuran,
                    ContentType = contentType is { Length: <= 100 } ? contentType : null,
                    CreatedAt = DateTime.UtcNow,
                });
                await _db.SaveChangesAsync();

                _logger.LogInformation("Produk {Nama} tersimpan (gambar R2: {Key})", input.NamaProduk, input.ObjectKey);
                return Json(new { ok = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Gagal menyimpan produk (R2)");
                return StatusCode(StatusCodes.Status500InternalServerError, new { error = $"Gagal menyimpan produk: {ex.Message}" });
            }
        }

        // ==================== Hapus produk ====================

        /// <summary>Hapus produk local: row di database + file gambarnya di wwwroot.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteLocal(int id)
        {
            var produk = await _db.ProdukLocal.FindAsync(id);
            if (produk is not null)
            {
                // Hapus file fisiknya; nama file di-guard Path.GetFileName (anti path traversal).
                var filePath = Path.Combine(_env.WebRootPath ?? "wwwroot", ProdukFolder, Path.GetFileName(produk.FileName));
                try
                {
                    if (System.IO.File.Exists(filePath))
                    {
                        System.IO.File.Delete(filePath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Gagal menghapus file {File} — row produk tetap dihapus", filePath);
                }

                _db.ProdukLocal.Remove(produk);
                await _db.SaveChangesAsync();
                _logger.LogInformation("Produk local {Id} ({Nama}) dihapus", id, produk.NamaProduk);
            }

            return RedirectToAction(nameof(Index), new { deleted = "local" });
        }

        /// <summary>Hapus produk R2: row di database + object gambarnya di bucket.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteR2(int id)
        {
            var produk = await _db.ProdukR2.FindAsync(id);
            if (produk is not null)
            {
                if (_r2.IsConfigured())
                {
                    try
                    {
                        using var client = _r2.CreateClient();
                        await client.DeleteObjectAsync(new DeleteObjectRequest
                        {
                            BucketName = _r2.GetSetting("BucketName"),
                            Key = produk.ObjectKey,
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Gagal menghapus object {Key} dari R2 — row produk tetap dihapus", produk.ObjectKey);
                    }
                }

                _db.ProdukR2.Remove(produk);
                await _db.SaveChangesAsync();
                _logger.LogInformation("Produk R2 {Id} ({Nama}) dihapus", id, produk.NamaProduk);
            }

            return RedirectToAction(nameof(Index), new { deleted = "r2" });
        }

        public sealed record ProdukR2Input(string NamaProduk, decimal Harga, string ObjectKey, long UkuranByte, string? ContentType);

        // ==================== Helpers ====================

        private async Task LoadListsAsync()
        {
            ViewBag.ProdukLocal = await _db.ProdukLocal
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync();

            var produkR2 = await _db.ProdukR2
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync();

            // URL gambar R2 dihitung saat render: public URL bila tersedia,
            // jika tidak presigned GET berumur pendek (bucket tidak public-read).
            var publicBaseUrl = _r2.GetPublicBaseUrl();
            if (!string.IsNullOrEmpty(publicBaseUrl))
            {
                foreach (var p in produkR2)
                {
                    p.ImageUrl = $"{publicBaseUrl}/{p.ObjectKey}";
                }
            }
            else if (_r2.IsConfigured())
            {
                using var client = _r2.CreateClient();
                foreach (var p in produkR2)
                {
                    try
                    {
                        p.ImageUrl = client.GetPreSignedURL(new GetPreSignedUrlRequest
                        {
                            BucketName = _r2.GetSetting("BucketName"),
                            Key = p.ObjectKey,
                            Verb = HttpVerb.GET,
                            Expires = DateTime.UtcNow.AddMinutes(PresignedViewMinutes),
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Gagal membuat presigned URL untuk {Key}", p.ObjectKey);
                    }
                }
            }

            ViewBag.ProdukR2 = produkR2;
        }
    }
}
