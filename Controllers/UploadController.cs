using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Mvc;
using SampleR2.Models;
using SampleR2.Services;
using System.Text.RegularExpressions;

namespace SampleR2.Controllers
{
    /// <summary>
    /// Sample upload file ke Cloudflare R2 dan ke folder wwwroot/uploads.
    /// </summary>
    public class UploadController : Controller
    {
        private const string UploadFolder = "uploads";

        private readonly IWebHostEnvironment _env;
        private readonly IConfiguration _config;
        private readonly ILogger<UploadController> _logger;
        private readonly R2StorageService _r2;

        public UploadController(IWebHostEnvironment env, IConfiguration config, ILogger<UploadController> logger, R2StorageService r2)
        {
            _env = env;
            _config = config;
            _logger = logger;
            _r2 = r2;
        }

        // ==================== Cloudflare R2 ====================

        [HttpGet]
        public async Task<IActionResult> R2()
        {
            await LoadR2FileListAsync();
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> R2(IFormFile file)
        {
            if (file is null || file.Length == 0)
            {
                ModelState.AddModelError(string.Empty, "Silakan pilih file terlebih dahulu.");
            }
            else if (file.Length > 100_000_000)
            {
                ModelState.AddModelError(string.Empty, "Ukuran file maksimal 100 MB.");
            }
            else
            {
                try
                {
                    var key = $"uploads/{DateTime.UtcNow:yyyy/MM/dd}/{FileNamer.BuildUniqueFileName(file.FileName)}";

                    await using var stream = file.OpenReadStream();
                    using var client = _r2.CreateClient();
                    await client.PutObjectAsync(new PutObjectRequest
                    {
                        BucketName = _r2.GetSetting("BucketName"),
                        Key = key,
                        InputStream = stream,
                        ContentType = file.ContentType,
                        // R2 tidak mendukung streaming payload signing dengan trailer (default SDK v4).
                        DisablePayloadSigning = true,
                    });

                    ViewBag.UploadedKey = key;
                    ViewBag.UploadedSizeText = FileItemViewModel.FormatSize(file.Length);
                    _logger.LogInformation("File {FileName} berhasil diupload ke R2 dengan key {Key}", file.FileName, key);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Upload ke Cloudflare R2 gagal");
                    ModelState.AddModelError(string.Empty, $"Upload ke R2 gagal: {ex.Message}");
                }
            }

            await LoadR2FileListAsync();
            return View();
        }

        // ==================== Direct (browser → R2 via presigned URL) ====================

        private const int DirectUploadMaxBytes = 10 * 1024 * 1024; // 10 MB
        private static readonly TimeSpan PresignExpiry = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan PresignedViewExpiry = TimeSpan.FromHours(1);

        [HttpGet]
        public IActionResult Direct()
        {
            ViewBag.DirectConfigured = _r2.IsConfigured();
            ViewBag.DirectMaxBytes = DirectUploadMaxBytes;
            ViewBag.DirectMaxSizeText = FileItemViewModel.FormatSize(DirectUploadMaxBytes);
            ViewBag.PublicBaseUrl = _r2.GetPublicBaseUrl();
            return View();
        }

        /// <summary>
        /// Menerbitkan presigned PUT URL berumur pendek agar browser bisa mengupload langsung ke R2;
        /// byte file tidak pernah melewati backend. Secret key tetap di server, URL hanya berlaku
        /// untuk satu key, satu content type, dan kedaluwarsa otomatis.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PresignDirect([FromBody] PresignDirectRequest request)
        {
            if (!_r2.IsConfigured())
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    new { error = "Konfigurasi R2 belum lengkap." });
            }

            // Content type ditentukan server dari ekstensi (whitelist), bukan diambil dari FE.
            var contentType = ImageTypes.ResolveContentType(request?.FileName);
            if (contentType is null)
            {
                return BadRequest(new
                {
                    error = $"Hanya gambar ({string.Join(", ", ImageTypes.Extensions)}) yang diizinkan.",
                });
            }

            // Ukuran diperiksa dari nilai yang dideklarasikan FE (file asli tidak lewat backend).
            if (request!.Size is < 1 or > DirectUploadMaxBytes)
            {
                return BadRequest(new
                {
                    error = $"Ukuran file harus antara 1 byte dan {FileItemViewModel.FormatSize(DirectUploadMaxBytes)}.",
                });
            }

            try
            {
                // Key dibangun sepenuhnya di server: path traversal / key sembarangan tidak mungkin.
                var key = $"uploads/{DateTime.UtcNow:yyyy/MM/dd}/{FileNamer.BuildUniqueFileName(request.FileName)}";

                using var client = _r2.CreateClient();
                var putUrl = client.GetPreSignedURL(new GetPreSignedUrlRequest
                {
                    BucketName = _r2.GetSetting("BucketName"),
                    Key = key,
                    Verb = HttpVerb.PUT,
                    ContentType = contentType,
                    Expires = DateTime.UtcNow.Add(PresignExpiry),
                });

                // URL untuk melihat/preview hasil upload (bucket tidak public-read).
                var viewUrl = client.GetPreSignedURL(new GetPreSignedUrlRequest
                {
                    BucketName = _r2.GetSetting("BucketName"),
                    Key = key,
                    Verb = HttpVerb.GET,
                    Expires = DateTime.UtcNow.Add(PresignedViewExpiry),
                });

                var publicBaseUrl = _r2.GetPublicBaseUrl();
                _logger.LogInformation("Menerbitkan presigned URL untuk {Key} ({ContentType}, {Size} byte)", key, contentType, request.Size);

                return Json(new
                {
                    url = putUrl,
                    viewUrl,
                    key,
                    contentType,
                    expiresIn = (int)PresignExpiry.TotalSeconds,
                    publicUrl = string.IsNullOrEmpty(publicBaseUrl) ? null : $"{publicBaseUrl}/{key}",
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Gagal menerbitkan presigned URL R2");
                return StatusCode(StatusCodes.Status500InternalServerError,
                    new { error = $"Gagal menerbitkan presigned URL: {ex.Message}" });
            }
        }

        public sealed record PresignDirectRequest(string FileName, long Size);

        // ==================== wwwroot ====================

        [HttpGet]
        public IActionResult Wwwroot()
        {
            LoadWwwrootFileList();
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Wwwroot(IFormFile file)
        {
            if (file is null || file.Length == 0)
            {
                ModelState.AddModelError(string.Empty, "Silakan pilih file terlebih dahulu.");
            }
            else if (file.Length > 100_000_000)
            {
                ModelState.AddModelError(string.Empty, "Ukuran file maksimal 100 MB.");
            }
            else
            {
                try
                {
                    var uploadsFolder = Path.Combine(_env.WebRootPath ?? "wwwroot", UploadFolder);
                    Directory.CreateDirectory(uploadsFolder);

                    var fileName = FileNamer.BuildUniqueFileName(file.FileName);
                    var filePath = Path.Combine(uploadsFolder, fileName);

                    await using (var stream = new FileStream(filePath, FileMode.Create))
                    {
                        await file.CopyToAsync(stream);
                    }

                    ViewBag.UploadedName = fileName;
                    ViewBag.UploadedUrl = $"/{UploadFolder}/{fileName}";
                    ViewBag.UploadedSizeText = FileItemViewModel.FormatSize(file.Length);
                    _logger.LogInformation("File {FileName} berhasil diupload ke wwwroot sebagai {SavedName}", file.FileName, fileName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Upload ke wwwroot gagal");
                    ModelState.AddModelError(string.Empty, $"Upload ke wwwroot gagal: {ex.Message}");
                }
            }

            LoadWwwrootFileList();
            return View();
        }

        // ==================== Helpers ====================

        private async Task LoadR2FileListAsync()
        {
            var files = new List<FileItemViewModel>();

            if (_r2.IsConfigured())
            {
                try
                {
                    using var client = _r2.CreateClient();
                    var response = await client.ListObjectsV2Async(new ListObjectsV2Request
                    {
                        BucketName = _r2.GetSetting("BucketName"),
                        Prefix = "uploads/",
                    });

                    var publicBaseUrl = _r2.GetPublicBaseUrl();

                    // Catatan: S3Objects bernilai null (bukan list kosong) jika tidak ada object dengan prefix tersebut.
                    foreach (var obj in response.S3Objects ?? Enumerable.Empty<S3Object>())
                    {
                        files.Add(new FileItemViewModel
                        {
                            Name = obj.Key,
                            Size = obj.Size ?? 0,
                            LastModified = obj.LastModified?.ToUniversalTime() ?? DateTime.UtcNow,
                            Url = string.IsNullOrEmpty(publicBaseUrl) ? null : $"{publicBaseUrl}/{obj.Key}",
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Gagal mengambil daftar file dari R2");
                    ViewBag.R2ListError = ex.Message;
                }
            }

            ViewBag.R2Configured = _r2.IsConfigured();
            ViewBag.R2Files = files.OrderByDescending(f => f.LastModified).ToList();
        }

        private void LoadWwwrootFileList()
        {
            var files = new List<FileItemViewModel>();
            var uploadsFolder = Path.Combine(_env.WebRootPath ?? "wwwroot", UploadFolder);

            if (Directory.Exists(uploadsFolder))
            {
                foreach (var path in Directory.EnumerateFiles(uploadsFolder))
                {
                    var info = new FileInfo(path);
                    files.Add(new FileItemViewModel
                    {
                        Name = info.Name,
                        Size = info.Length,
                        LastModified = info.LastWriteTimeUtc,
                        Url = $"/{UploadFolder}/{info.Name}",
                    });
                }
            }

            ViewBag.WwwrootFiles = files.OrderByDescending(f => f.LastModified).ToList();
        }
    }
}
