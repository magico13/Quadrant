using System.Text.Json;
using Quadrant.Server.Sessions;

var builder = WebApplication.CreateBuilder(args);

// Register services.
builder.Services.AddSingleton<SessionManager>();

// Ship catalog: load weapons and ships from JSON at startup.
var contentRoot = builder.Environment.ContentRootPath;
var shipCatalog = new ShipCatalogService();
shipCatalog.LoadWeapons(Path.Combine(contentRoot, "weapons"));
shipCatalog.LoadShips(Path.Combine(contentRoot, "ships"));
builder.Services.AddSingleton(shipCatalog);

builder.Services.AddSignalR();
builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

var app = builder.Build();

// Serve static client files (wwwroot).
// No-cache for static assets so frontend edits are picked up on refresh during dev.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
        ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate"
});

// REST API controllers.
app.MapControllers();

// Fallback to index.html for SPA-style routing.
app.MapFallbackToFile("index.html");

// Health check.
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", activeGames = app.Services.GetRequiredService<SessionManager>().Count }));

app.Run();
