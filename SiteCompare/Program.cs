using SiteCompare.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

// HTTP client for sitemap fetching
builder.Services.AddHttpClient("SitemapClient", client =>
{
    client.DefaultRequestHeaders.Add("User-Agent", "SiteCompare/1.0 (+https://github.com/BartDM/SiteCompare)");
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Application services
builder.Services.AddSingleton<IComparisonJobService, ComparisonJobService>();
builder.Services.AddSingleton<IScreenshotService, ScreenshotService>();
builder.Services.AddTransient<ISitemapService, SitemapService>();
builder.Services.AddTransient<IImageComparisonService, ImageComparisonService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
