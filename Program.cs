using Microsoft.EntityFrameworkCore;
using SampleR2.Data;
using SampleR2.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddSingleton<R2StorageService>();
// Naikkan limit body Kestrel supaya Kestrel tidak memutus koneksi di tengah streaming body —
// di balik Cloudflare Tunnel itu tampil sebagai 413/502 di client. Batas per-endpoint tetap
// divalidasi di controller (Produk 50 MB, Upload 100 MB, Benchmark 5 GB) dengan pesan error yang jelas.
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 5L * 1024 * 1024 * 1024; // 5 GiB — batas single PUT R2
});
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

var app = builder.Build();

// Buat tabel produk jika belum ada (sample; untuk produksi gunakan EF migrations).
// Lewati bila connection string belum diisi (mis. image dijalankan tanpa env var).
using (var scope = app.Services.CreateScope())
{
    var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    if (!string.IsNullOrEmpty(config.GetConnectionString("Default")))
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();
    }
    else
    {
        app.Logger.LogWarning("ConnectionStrings:Default belum diset — tabel produk tidak dibuat. Isi via env var ConnectionStrings__Default.");
    }
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
// Melayani file dari disk saat runtime (hasil upload local storage & benchmark).
// MapStaticAssets hanya melayani file yang sudah ada saat build — file baru akan 404 tanpa ini.
app.UseStaticFiles();
app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();
