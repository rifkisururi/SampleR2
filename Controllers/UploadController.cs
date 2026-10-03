using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Mvc;
using SampleR2.Models;
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

        public UploadController(IWebHostEnvironment env, IConfiguration config, ILogger<UploadController> logger)
        {
            _env = env;
            _config = config;
            _logger = logger;
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
        [RequestSizeLimit(100_000_000)] // 100 MB
        public async Task<IActionResult> R2(IFormFile file)
        {
            if (file is null || file.Length == 0)
            {
                ModelState.AddModelError(string.Empty, "Silakan pilih file terlebih dahulu.");
            }
            else
            {
                try
                {
                    var key = $"uploads/{DateTime.UtcNow:yyyy/MM/dd}/{BuildUniqueFileName(file.FileName)}";

                    await using var stream = file.OpenReadStream();
                    using var client = CreateR2Client();
                    await client.PutObjectAsync(new PutObjectRequest
                    {
                        BucketName = GetR2Setting("BucketName"),
                        Key = key,
                        InputStream = stream,
                        ContentType = file.ContentType,
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

        // ==================== wwwroot ====================

        [HttpGet]
        public IActionResult Wwwroot()
        {
            LoadWwwrootFileList();
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(100_000_000)] // 100 MB
        public async Task<IActionResult> Wwwroot(IFormFile file)
        {
            if (file is null || file.Length == 0)
            {
                ModelState.AddModelError(string.Empty, "Silakan pilih file terlebih dahulu.");
            }
            else
            {
                try
                {
                    var uploadsFolder = Path.Combine(_env.WebRootPath ?? "wwwroot", UploadFolder);
                    Directory.CreateDirectory(uploadsFolder);

                    var fileName = BuildUniqueFileName(file.FileName);
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

        private AmazonS3Client CreateR2Client()
        {
            var accountId = GetR2Setting("AccountId");
            var accessKey = GetR2Setting("AccessKeyId");
            var secretKey = GetR2Setting("SecretAccessKey");

            // Cloudflare R2 kompatibel dengan S3 API.
            // Endpoint: https://<accountid>.r2.cloudflarestorage.com
            var config = new AmazonS3Config
            {
                ServiceURL = $"https://{accountId}.r2.cloudflarestorage.com",
                ForcePathStyle = true,
                AuthenticationRegion = "auto",
            };

            return new AmazonS3Client(accessKey, secretKey, config);
        }

        private bool IsR2Configured() =>
            !string.IsNullOrWhiteSpace(_config["R2:AccountId"]) &&
            !string.IsNullOrWhiteSpace(_config["R2:AccessKeyId"]) &&
            !string.IsNullOrWhiteSpace(_config["R2:SecretAccessKey"]) &&
            !string.IsNullOrWhiteSpace(_config["R2:BucketName"]);

        private string GetR2Setting(string key)
        {
            var value = _config[$"R2:{key}"];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    $"Konfigurasi R2:{key} belum diisi. Lengkapi di appsettings.json atau gunakan 'dotnet user-secrets set \"R2:{key}\" \"nilai\"'.");
            }

            return value;
        }

        private async Task LoadR2FileListAsync()
        {
            var files = new List<FileItemViewModel>();

            if (IsR2Configured())
            {
                try
                {
                    using var client = CreateR2Client();
                    var response = await client.ListObjectsV2Async(new ListObjectsV2Request
                    {
                        BucketName = GetR2Setting("BucketName"),
                        Prefix = "uploads/",
                    });

                    var publicBaseUrl = _config["R2:PublicBaseUrl"]?.TrimEnd('/');

                    foreach (var obj in response.S3Objects)
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

            ViewBag.R2Configured = IsR2Configured();
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

        /// <summary>
        /// Menghasilkan nama file unik: {guid}-{nama-asli-yang-diamankan}{ekstensi}.
        /// Mencegah path traversal dan tabrakan nama file.
        /// </summary>
        private static string BuildUniqueFileName(string originalFileName)
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
}
