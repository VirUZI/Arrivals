using Arrivals.Clients;
using Arrivals.Infrastructure;
using Arrivals.Options;
using Arrivals.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddAuthorization();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ResponseCache>();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.Configure<DashboardOptions>(
    builder.Configuration.GetSection(DashboardOptions.SectionName));
builder.Services.Configure<YamtrackOptions>(
    builder.Configuration.GetSection(YamtrackOptions.SectionName));
builder.Services.Configure<SonarrOptions>(
    builder.Configuration.GetSection(SonarrOptions.SectionName));
builder.Services.Configure<RadarrOptions>(
    builder.Configuration.GetSection(RadarrOptions.SectionName));
builder.Services.Configure<JellyfinOptions>(
    builder.Configuration.GetSection(JellyfinOptions.SectionName));

builder.Services.AddHttpClient<YamtrackClient>();
builder.Services.AddHttpClient<SonarrClient>();
builder.Services.AddHttpClient<RadarrClient>();
builder.Services.AddHttpClient<JellyfinClient>();
builder.Services.AddScoped<ArrivalsService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    await next();
});

app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();
app.MapRazorPages();
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.Run();
