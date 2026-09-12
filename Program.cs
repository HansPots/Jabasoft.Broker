using System.Text.Json.Serialization;
using Jabasoft.Base.AiBroker;
using Jabasoft.Broker;

var builder = WebApplication.CreateBuilder(args);

// Accept/return AiProvider as "Ollama"/"LmStudio" in JSON bodies, matching
// how it's already written in query strings (GET /api/models) - without
// this, request bodies would need the enum's numeric value instead.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddHttpClient("chat", c => c.Timeout = TimeSpan.FromMinutes(5));
builder.Services.AddHttpClient("embed", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient("catalog", c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient("test", c => c.Timeout = TimeSpan.FromSeconds(30));

builder.Services.AddSingleton<ProviderClient>();
builder.Services.AddSingleton<RequestGate>();

var app = builder.Build();

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

app.MapPost("/api/chat", async (ChatRequest request, ProviderClient client, RequestGate gate, CancellationToken ct) =>
{
    var result = await gate.RunGatedAsync(
        request.Provider,
        request.ServerUrl,
        () => client.ChatAsync(request.Provider, request.ServerUrl, request.Model, request.Messages, request.Temperature, ct),
        ct);

    return Results.Ok(result);
});

app.MapPost("/api/embed", async (EmbedRequest request, ProviderClient client, RequestGate gate, CancellationToken ct) =>
{
    var result = await gate.RunGatedAsync(
        request.Provider,
        request.ServerUrl,
        () => client.EmbedAsync(request.Provider, request.ServerUrl, request.Model, request.Text, ct),
        ct);

    return Results.Ok(result);
});

app.Run();
