# 📦 SampleR2

Sample aplikasi **ASP.NET Core MVC (.NET 10)** untuk mengeksplorasi **Cloudflare R2** (S3-compatible object storage)
dari berbagai arah: upload lewat backend, upload langsung dari browser (presigned URL), penyimpanan metadata di
**PostgreSQL (Neon)**, serta **benchmark upload** per jalur dengan file asli.

> 📝 Penjelasan mendalam tiap alur upload + cara pengamanannya: lihat [`noteUpload.md`](noteUpload.md).

---

## ✨ Fitur

| Halaman | Alur | Keterangan |
|---|---|---|
| `/Upload/Wwwroot` | 💾 Upload → **local storage** | File disimpan ke `wwwroot/uploads/`, dilayani sebagai static file |
| `/Upload/R2` | ☁️ Upload → **R2 via controller** | File melewati backend (`PutObjectAsync` AWS SDK for .NET) |
| `/Upload/Direct` | 🚀 Upload → **R2 langsung dari browser** | Presigned URL — file tidak melewati backend, progress bar per byte |
| `/Produk` | 📦 Skenario produk + **PostgreSQL** | 2 tabel: `produk_local` (gambar di wwwroot) & `produk_r2` (gambar langsung dari browser), lengkap tombol hapus (row + file/object) |
| `/Benchmark` | 🏁 Benchmark upload 3 jalur | Pilih **1 file asli**, atur jumlah putaran, jalankan per target (wwwroot / R2 server / R2 browser), hasil dibandingkan dalam satu tabel. Jalur browser punya `console.log` per tahap (prefix `[Bench]`) |

## 🛠 Tech Stack

- **.NET 10** — ASP.NET Core MVC
- **AWSSDK.S3 v4** — interoperabilitas S3 dengan Cloudflare R2
- **EF Core 10 + Npgsql** — PostgreSQL (Neon), skema dibuat via `EnsureCreated()` (untuk produksi gunakan migrations)
- **Bootstrap 5** — UI
- Vanilla JS untuk alur FE (fetch/XHR, `performance.now()` untuk pengukuran)

## ⚙️ Konfigurasi

Isi via `appsettings.json` (dev lokal) — **atau lebih aman** via `dotnet user-secrets` / environment variable:

```json
{
  "ConnectionStrings": {
    "Default": "Host=<neon-host>;Database=neondb;Username=<user>;Password=<pass>;SSL Mode=Require"
  },
  "R2": {
    "AccountId": "<cloudflare-account-id>",
    "AccessKeyId": "<r2-access-key-id>",
    "SecretAccessKey": "<r2-secret-access-key>",
    "BucketName": "<nama-bucket>",
    "PublicBaseUrl": ""   // opsional: https://pub-xxxx.r2.dev — kosongkan jika bucket privat (pakai presigned GET)
  }
}
```

Env var yang setara (dipakai di container): `ConnectionStrings__Default`, `R2__AccountId`, `R2__AccessKeyId`,
`R2__SecretAccessKey`, `R2__BucketName`, `R2__PublicBaseUrl`.

> ⚠️ `appsettings.json` sengaja **tidak ikut ke image Docker** (lihat `.dockerignore`) — kredensial selalu disuplai saat runtime.

## 🚀 Menjalankan

### Lokal

```bash
dotnet run
# buka http://localhost:<port> (lihat launchSettings.json)
```

### Docker (single container)

```bash
docker run -d -p 8080:8080 --name sampler2 \
  -e ConnectionStrings__Default="Host=...;Database=neondb;Username=...;Password=...;SSL Mode=Require" \
  -e R2__AccountId=<id> -e R2__AccessKeyId=<key> -e R2__SecretAccessKey=<secret> \
  -e R2__BucketName=<bucket> \
  rifkisururi/sampler2:latest
```

Tanpa env var, app tetap hidup (fitur DB/R2 nonaktif dengan warning di log).

### Docker Compose (multi container, 1 server)

`compose.yaml` + `sampler2.env` (berisi kredensial — jangan di-commit):

```bash
docker compose up -d      # jalankan semua container
docker compose ps         # status
docker compose logs -f    # log gabungan
docker compose pull && docker compose up -d   # update ke image terbaru
```

Mapping port saat ini (di VPS `thinkcentre`):

| Container | Port host | Catatan |
|---|---|---|
| sampler2-1 | 8081 | |
| sampler2-2 | **8085** | 8082 dipakai aplikasi lain |
| sampler2-3 | 8083 | |
| sampler2-4 | 8084 | |

Keempatnya share **named volume `uploads`** (`/app/wwwroot/uploads`) supaya konten `produk_local` konsisten antar instance.
Container berjalan sebagai **non-root user** — folder yang butuh tulisan sudah di-`chown` di Dockerfile.

## 🔐 Setup R2 sekali jalan (CORS)

Upload langsung dari browser butuh CORS di bucket (dashboard Cloudflare → R2 → bucket → Settings → CORS policy):

```json
[
  {
    "AllowedOrigins": ["http://localhost:5125", "https://domain-anda.com"],
    "AllowedMethods": ["GET", "PUT"],
    "AllowedHeaders": ["content-type"],
    "MaxAgeSeconds": 300
  }
]
```

Sesuaikan `AllowedOrigins` dengan origin aplikasi.

## 🛡 Catatan Keamanan (ringkas)

- **Secret key tidak pernah keluar dari server** — upload FE memakai presigned URL berumur pendek (10 menit),
  terikat 1 key + 1 content type (ter-sign di `X-Amz-SignedHeaders`)
- **Anti-forgery token (CSRF)** wajib untuk semua endpoint POST, termasuk penerbitan presigned URL
- **Nama file/key dibangun server** (`Services/FileNamer.cs`) — anti path traversal & tabrakan
- **Whitelist ekstensi + content type dari server** (`Services/ImageTypes.cs`), magic bytes dicek di FE
- **Verifikasi server setelah upload FE**: `HeadObject` ke R2 + ukuran diambil dari bucket, key divalidasi regex
  sebelum direkam ke database
- Batas ukuran: Produk 50 MB · Upload 100 MB · Benchmark 5 GB (batas single PUT R2) — divalidasi di **controller**
  dengan pesan rapi; Kestrel global 5 GB supaya koneksi tidak diputus mid-stream (di balik Cloudflare Tunnel itu
  tampil sebagai 413/502)
- `UseStaticFiles()` diaktifkan agar file yang diupload runtime tetap dilayani (`MapStaticAssets` hanya melayani
  file yang ada saat build)

## 📁 Struktur Penting

```
Controllers/
  UploadController.cs    # halaman R2, Wwwroot, Direct (FE) + PresignDirect
  ProdukController.cs    # halaman produk (local + R2) + delete
  BenchmarkController.cs # benchmark per target + PresignBenchmark + CleanupFe
Services/
  R2StorageService.cs    # konfigurasi + client S3 ke R2
  FileNamer.cs           # nama file unik yang aman
  ImageTypes.cs          # whitelist gambar → content type
Data/AppDbContext.cs      # DbContext PostgreSQL (tabel produk_local, produk_r2)
Views/Upload, Views/Produk, Views/Benchmark
noteUpload.md             # dokumentasi detail alur upload + security
```

## 🐳 Image Docker

`docker.io/rifkisururi/sampler2:latest` — multi-stage build (`mcr.microsoft.com/dotnet/aspnet:10.0`),
listen di port **8080** dalam container.
