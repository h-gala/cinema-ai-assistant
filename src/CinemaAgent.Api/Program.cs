using System.Security.Cryptography;
using System.Text;
using CinemaAgent.Storage;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("Redis")
        ?? throw new InvalidOperationException("ConnectionStrings:Redis is missing")));
builder.Services.AddSingleton<ICinemaStore>(sp =>
    new RedisCinemaStore(sp.GetRequiredService<IConnectionMultiplexer>()));

var app = builder.Build();

// Simple API-key auth (everything except /health)
var expectedKey = Encoding.UTF8.GetBytes(builder.Configuration["Api:Key"] ?? "");
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/health")) { await next(); return; }

    var provided = Encoding.UTF8.GetBytes(ctx.Request.Headers["X-Api-Key"].ToString());
    if (expectedKey.Length == 0 || provided.Length != expectedKey.Length ||
        !CryptographicOperations.FixedTimeEquals(provided, expectedKey))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    await next();
});

app.MapGet("/health", () => Results.Ok());

app.MapGet("/schedule-keys", async (ICinemaStore store, CancellationToken ct) =>
    Results.Ok(await store.ScheduleKeysAsync(ct)));

app.MapGet("/movie-keys", async (ICinemaStore store, CancellationToken ct) =>
    Results.Ok(await store.MovieKeysAsync(ct)));

// POST because the key list can be too long for a query string
app.MapPost("/documents", async (ReadRequest req, ICinemaStore store, CancellationToken ct) =>
    Results.Ok(await store.ReadJsonAsync(req.Keys, ct)));

app.Run();

record ReadRequest(string[] Keys);