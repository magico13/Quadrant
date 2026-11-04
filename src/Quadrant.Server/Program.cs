using System.Text.Json;
using Quadrant.Server.Sessions;

var builder = WebApplication.CreateBuilder(args);

// Register services.
var databasePath = Environment.GetEnvironmentVariable("QUADRANT_DB_PATH")
    ?? Path.Combine(builder.Environment.ContentRootPath, "data", "quadrant.db");
builder.Services.AddSingleton(new SessionManager(databasePath));

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

// Serialize each room's requests so state changes and their snapshots stay together.
app.Use(async (context, next) =>
{
    var parts = context.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
    string? room = parts.Length >= 4 && parts[0] == "api" && parts[1] == "game"
        ? parts[2] == "state" ? parts[3] : parts[2]
        : null;
    var manager = context.RequestServices.GetRequiredService<SessionManager>();
    var session = room is null ? null : manager.Get(room);
    if (session is null) { await next(); return; }
    await session.Gate.WaitAsync(context.RequestAborted);
    var originalBody = context.Response.Body;
    await using var buffer = new MemoryStream();
    context.Response.Body = buffer;
    try
    {
        await next();
        manager.Save(session, HttpMethods.IsPost(context.Request.Method) && context.Response.StatusCode < 400);
        buffer.Position = 0;
        await buffer.CopyToAsync(originalBody, context.RequestAborted);
    }
    finally { context.Response.Body = originalBody; session.Gate.Release(); }
});

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
