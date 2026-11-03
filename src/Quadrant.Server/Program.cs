using System.Text.Json;
using Quadrant.Server.Sessions;

var builder = WebApplication.CreateBuilder(args);

// Register services.
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSignalR();
builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

var app = builder.Build();

// Serve static client files (wwwroot).
app.UseStaticFiles();

// REST API controllers.
app.MapControllers();

// Fallback to index.html for SPA-style routing.
app.MapFallbackToFile("index.html");

// Health check.
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", activeGames = app.Services.GetRequiredService<SessionManager>().Count }));

app.Run();
