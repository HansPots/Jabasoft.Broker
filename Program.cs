using System.Text.Json.Serialization;
using Jabasoft.Base.AiBroker;
using Jabasoft.Broker;
using Microsoft.EntityFrameworkCore;
using Shared.Telemetry;

var builder = WebApplication.CreateBuilder(args);

// Accept/return AiProvider as "Ollama"/"LmStudio" in JSON bodies, matching
// how it's already written in query strings (GET /api/models) - without
// this, request bodies would need the enum's numeric value instead.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Same connection-string pattern as every other JabaSoft app (see
// Shared.Telemetry/TelemetryDbContextFactory.cs): env var first, then
// appsettings.json's ConnectionStrings:JabasoftBase, then a hardcoded
// local-SQL-Server fallback.
var telemetryConnectionString =
    Environment.GetEnvironmentVariable("JABASOFT_TELEMETRY_CONNECTION_STRING")
    ?? builder.Configuration.GetConnectionString("JabasoftBase")
    ?? "Server=localhost;Database=JabasoftBase;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;";

builder.Services.AddDbContext<TelemetryDbContext>(options => options.UseSqlServer(telemetryConnectionString));
builder.Services.AddScoped<ITokenUsageRepository, TokenUsageRepository>();

builder.Services.AddHttpClient("chat", c => c.Timeout = TimeSpan.FromMinutes(5));
builder.Services.AddHttpClient("embed", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient("catalog", c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient("test", c => c.Timeout = TimeSpan.FromSeconds(30));

builder.Services.AddSingleton<ProviderClient>();
builder.Services.AddSingleton<RequestGate>();

var app = builder.Build();

try
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<TelemetryDbContext>().Database.MigrateAsync();
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Could not apply database migrations for the shared JabasoftBase database. Start SQL Server and reload.");
}

app.MapGet("/health", () => Results.Ok());

app.MapGet("/api/models", async (AiProvider provider, string serverUrl, RequestGate gate, CancellationToken ct) =>
{
    var result = await gate.GetModelsAsync(provider, serverUrl, ct);
    return Results.Ok(result);
});

app.MapPost("/api/test-connection", async (TestConnectionRequest request, ProviderClient client, CancellationToken ct) =>
{
    var result = await client.TestAsync(request.Provider, request.ServerUrl, request.Model, ct);
    return Results.Ok(result);
});

app.MapPost("/api/chat", async (ChatRequest request, ProviderClient client, RequestGate gate, ITokenUsageRepository tokenUsage, CancellationToken ct) =>
{
    var result = await gate.RunGatedAsync(
        request.Provider,
        request.ServerUrl,
        () => client.ChatAsync(request.Provider, request.ServerUrl, request.Model, request.Messages, request.Temperature, ct),
        ct);

    if (result.Success)
    {
        await tokenUsage.RecordAsync(request.Application, request.Model, result.PromptTokens, result.CompletionTokens, ct);
    }

    return Results.Ok(result);
});

app.MapPost("/api/embed", async (EmbedRequest request, ProviderClient client, RequestGate gate, ITokenUsageRepository tokenUsage, CancellationToken ct) =>
{
    var result = await gate.RunGatedAsync(
        request.Provider,
        request.ServerUrl,
        () => client.EmbedAsync(request.Provider, request.ServerUrl, request.Model, request.Text, ct),
        ct);

    if (result.Success)
    {
        await tokenUsage.RecordAsync(request.Application, request.Model, result.TokensUsed, 0, ct);
    }

    return Results.Ok(result);
});

app.Run();
