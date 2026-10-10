using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Mvc;
using SampleR2.Models;
using SampleR2.Services;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SampleR2.Controllers
{
    /// <summary>
    /// Benchmark upload: membandingkan performa tulis ke Cloudflare R2 vs folder wwwroot.
    /// </summary>
    public class BenchmarkController : Controller
    {
        private const string WwwrootBenchmarkFolder = "uploads-benchmark";
        private const string R2BenchmarkPrefix = "benchmark/";

        // Batas single PUT ke R2 adalah 5 GiB (di atasnya wajib multipart upload API).
        private const long MaxBenchmarkFileBytes = 5L * 1024 * 1024 * 1024;

        // Key FE benchmark yang sah — hanya key keluaran PresignBenchmark (hindari hapus object sembarangan).
        private static readonly Regex FeBenchmarkKeyPattern = new(
            @"^benchmark/fe-[0-9a-f]{32}\.[a-z0-9]{1,9}$", RegexOptions.Compiled);

        private readonly IWebHostEnvironment _env;
        private readonly ILogger<BenchmarkController> _logger;
        private readonly R2StorageService _r2;

        public BenchmarkController(IWebHostEnvironment env, ILogger<BenchmarkController> logger, R2StorageService r2)
        {
            _env = env;
            _logger = logger;
            _r2 = r2;
        }

        [HttpGet]
        public IActionResult Index()
        {
            return View(new BenchmarkViewModel { Iterations = 10, SizeKb = 1024, R2Configured = _r2.IsConfigured() });
        }

        /// <summary>
        /// Menjalankan benchmark untuk SATU target (wwwroot / r2) — dipanggil AJAX dari tombol masing-masing,
        /// memakai FILE ASLI pilihan user sebagai payload (multipart/form-data).
        /// File dibaca ulang per putaran dari temp request (streaming) — memori server tetap kecil
        /// apa pun ukuran file. Hasil dikembalikan sebagai JSON untuk tabel hasil di browser.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(5L * 1024 * 1024 * 1024)] // 5 GiB — sama dengan batas single PUT R2
        public async Task<IActionResult> RunTarget(string target, int iterations, IFormFile file)
        {
            if (file is null || file.Length == 0)
            {
                return BadRequest(new { error = "Pilih file yang ingin dibenchmark." });
            }

            // Batasi agar benchmark tidak membakar resource.
            iterations = Math.Clamp(iterations, 1, 100);
            if (file.Length > MaxBenchmarkFileBytes)
            {
                return BadRequest(new { error = "Ukuran file maksimal 5 GB (batas single PUT R2)." });
            }

            // Setiap putaran membuka stream baru dari file yang sama (IFormFile di-buffer ke temp disk oleh framework).
            Func<Stream> openPayload = file.OpenReadStream;

            BenchmarkTargetResult result;
            switch (target)
            {
                case "wwwroot":
                    result = await BenchmarkWwwrootAsync(iterations, openPayload, file.Length);
                    break;

                case "r2":
                    if (!_r2.IsConfigured())
                    {
                        return StatusCode(StatusCodes.Status503ServiceUnavailable,
                            new { error = "R2 belum dikonfigurasi — jalankan target wwwroot atau lengkapi R2:AccessKeyId & R2:SecretAccessKey." });
                    }
                    result = await BenchmarkR2Async(iterations, openPayload, file.Length);
                    break;

                default:
                    return BadRequest(new { error = "Target tidak dikenal." });
            }

            return Json(result);
        }

        // ==================== Implementasi benchmark ====================

        private async Task<BenchmarkTargetResult> BenchmarkWwwrootAsync(int iterations, Func<Stream> openPayload, long payloadBytes)
        {
            var dir = Path.Combine(_env.WebRootPath ?? "wwwroot", WwwrootBenchmarkFolder);
            Directory.CreateDirectory(dir);

            var samples = new List<double>();
            int failed = 0;
            string? firstError = null;

            try
            {
                // Warmup: JIT, cold cache, dll. Tidak dihitung.
                await using (var src = openPayload())
                await using (var dst = new FileStream(Path.Combine(dir, $"{Guid.NewGuid():N}.bin"), FileMode.Create))
                {
                    await src.CopyToAsync(dst);
                }

                for (var i = 0; i < iterations; i++)
                {
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        await using (var src = openPayload())
                        await using (var dst = new FileStream(Path.Combine(dir, $"{Guid.NewGuid():N}.bin"), FileMode.Create))
                        {
                            await src.CopyToAsync(dst);
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        firstError ??= ex.Message;
                        _logger.LogError(ex, "Benchmark wwwroot gagal pada iterasi {Iteration}", i);
                    }

                    sw.Stop();
                    samples.Add(sw.Elapsed.TotalMilliseconds);
                }
            }
            finally
            {
                TryDeleteDirectory(dir);
            }

            return BenchmarkTargetResult.Build("wwwroot (disk lokal)", payloadBytes, samples, failed, firstError);
        }

        private async Task<BenchmarkTargetResult> BenchmarkR2Async(int iterations, Func<Stream> openPayload, long payloadBytes)
        {
            var samples = new List<double>();
            int failed = 0;
            string? firstError = null;
            var uploadedKeys = new List<string>();

            try
            {
                var bucket = _r2.GetSetting("BucketName");
                using var client = _r2.CreateClient(); // satu client dipakai ulang: connection pool tetap hangat

                // Warmup: DNS + TLS handshake + SigV4. Tidak dihitung.
                var warmupKey = $"{R2BenchmarkPrefix}warmup-{Guid.NewGuid():N}.bin";
                await PutObjectAsync(client, bucket, warmupKey, openPayload, payloadBytes);
                uploadedKeys.Add(warmupKey);

                for (var i = 0; i < iterations; i++)
                {
                    var key = $"{R2BenchmarkPrefix}run-{Guid.NewGuid():N}.bin";
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        await PutObjectAsync(client, bucket, key, openPayload, payloadBytes);
                        uploadedKeys.Add(key);
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        firstError ??= ex.Message;
                        _logger.LogError(ex, "Benchmark R2 gagal pada iterasi {Iteration}", i);
                    }

                    sw.Stop();
                    samples.Add(sw.Elapsed.TotalMilliseconds);
                }
            }
            catch (Exception ex)
            {
                firstError ??= ex.Message;
                _logger.LogError(ex, "Benchmark R2 gagal disiapkan");
            }
            finally
            {
                await TryDeleteR2ObjectsAsync(uploadedKeys);
            }

            return BenchmarkTargetResult.Build("Cloudflare R2", payloadBytes, samples, failed, firstError);
        }

        // ==================== Benchmark dari browser (FE, presigned URL) ====================

        /// <summary>
        /// Menerbitkan presigned PUT untuk benchmark dari browser memakai FILE ASLI:
        /// ekstensi & content type mengikuti file, key di bawah prefix benchmark/,
        /// kedaluwarsa singkat. Browser mengukur waktunya sendiri.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PresignBenchmark([FromBody] PresignBenchmarkRequest request)
        {
            if (!_r2.IsConfigured())
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Konfigurasi R2 belum lengkap." });
            }

            if (request is null || request.SizeBytes is < 1 or > MaxBenchmarkFileBytes)
            {
                return BadRequest(new { error = "Ukuran file maksimal 5 GB (batas single PUT R2)." });
            }

            // Ekstensi & content type disanitasi ketat (masuk ke key object dan header request).
            var ext = Path.GetExtension(Path.GetFileName(request.FileName ?? string.Empty)).ToLowerInvariant();
            if (ext.Length > 10 || !Regex.IsMatch(ext, @"^\.[a-z0-9]{1,9}$"))
            {
                ext = ".bin";
            }

            var contentType = string.IsNullOrWhiteSpace(request.ContentType)
                ? "application/octet-stream"
                : request.ContentType.Trim();
            if (!Regex.IsMatch(contentType, @"^[a-zA-Z0-9][a-zA-Z0-9\-./;= ]{0,99}$"))
            {
                contentType = "application/octet-stream";
            }

            var key = $"{R2BenchmarkPrefix}fe-{Guid.NewGuid():N}{ext}";
            using var client = _r2.CreateClient();
            var url = client.GetPreSignedURL(new GetPreSignedUrlRequest
            {
                BucketName = _r2.GetSetting("BucketName"),
                Key = key,
                Verb = HttpVerb.PUT,
                ContentType = contentType,
                Expires = DateTime.UtcNow.AddMinutes(5),
            });

            return Json(new { url, key, contentType });
        }

        /// <summary>Dipanggil browser setelah benchmark selesai untuk membersihkan object hasil benchmark FE.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CleanupFe([FromBody] string[] keys)
        {
            if (keys is { Length: > 0 })
            {
                var valid = keys.Where(k => FeBenchmarkKeyPattern.IsMatch(k)).ToList();
                if (valid.Count > 0)
                {
                    await TryDeleteR2ObjectsAsync(valid);
                }
            }

            return Json(new { ok = true });
        }

        public sealed record PresignBenchmarkRequest(long SizeBytes, string? FileName, string? ContentType);

        // ==================== Helpers ====================

        private static async Task PutObjectAsync(AmazonS3Client client, string bucket, string key, Func<Stream> openPayload, long payloadBytes)
        {
            await using var stream = openPayload();
            var request = new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                InputStream = stream,
                ContentType = "application/octet-stream",
                // R2 tidak mendukung streaming payload signing dengan trailer (default SDK v4).
                DisablePayloadSigning = true,
            };
            // Stream non-seekable: SDK perlu tahu panjang payload lewat header Content-Length.
            request.Headers.ContentLength = payloadBytes;
            await client.PutObjectAsync(request);
        }

        private async Task TryDeleteR2ObjectsAsync(List<string> keys)
        {
            if (keys.Count == 0)
            {
                return;
            }

            try
            {
                using var client = _r2.CreateClient();
                var bucket = _r2.GetSetting("BucketName");

                foreach (var key in keys)
                {
                    await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Gagal membersihkan {Count} object benchmark di R2 (prefix {Prefix})", keys.Count, R2BenchmarkPrefix);
            }
        }

        private void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Gagal membersihkan folder benchmark {Path}", path);
            }
        }
    }
}
