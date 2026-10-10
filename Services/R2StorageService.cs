using Amazon.S3;

namespace SampleR2.Services
{
    /// <summary>
    /// Pembungkus konfigurasi dan client Cloudflare R2 (S3-compatible).
    /// Konfigurasi dibaca dari section "R2" di appsettings.json / user secrets.
    /// </summary>
    public class R2StorageService
    {
        private readonly IConfiguration _config;

        public R2StorageService(IConfiguration config)
        {
            _config = config;
        }

        public bool IsConfigured() =>
            !string.IsNullOrWhiteSpace(_config["R2:AccountId"]) &&
            !string.IsNullOrWhiteSpace(_config["R2:AccessKeyId"]) &&
            !string.IsNullOrWhiteSpace(_config["R2:SecretAccessKey"]) &&
            !string.IsNullOrWhiteSpace(_config["R2:BucketName"]);

        public string GetSetting(string key)
        {
            var value = _config[$"R2:{key}"];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    $"Konfigurasi R2:{key} belum diisi. Lengkapi di appsettings.json atau gunakan 'dotnet user-secrets set \"R2:{key}\" \"nilai\"'.");
            }

            return value;
        }

        /// <summary>
        /// Membuat client S3 yang mengarah ke endpoint R2.
        /// Caller bertanggung jawab melakukan dispose.
        /// </summary>
        public AmazonS3Client CreateClient()
        {
            var accountId = GetSetting("AccountId");
            var accessKey = GetSetting("AccessKeyId");
            var secretKey = GetSetting("SecretAccessKey");

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

        /// <summary>Base URL publik bucket (r2.dev / custom domain), atau null jika tidak diisi.</summary>
        public string? GetPublicBaseUrl() => _config["R2:PublicBaseUrl"]?.TrimEnd('/');
    }
}
