using System.Text.Json.Serialization;
using Jabasoft.Base.AiBroker;
using Jabasoft.Base.Logging;
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

// Wat de broker doet, in het geheugen, zodat elke applicatie het kan
// ophalen en in zijn Activity-blok tonen. Niet via de console-uitvoer:
// die is alleen te zien door wie het proces zelf gestart heeft, en de
// broker wordt juist gedeeld door meerdere apps.
builder.Services.AddSingleton<ActivityLog>();

builder.Services.AddSingleton<ProviderClient>();
builder.Services.AddSingleton<RequestGate>();

var app = builder.Build();

var activity = app.Services.GetRequiredService<ActivityLog>();
activity.Add("broker", "Broker gestart");

// Elke aanroep komt in het overzicht, behalve /health en /logs zelf: die
// worden elke paar seconden bevraagd en zouden al het andere wegdrukken.
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    var stil = path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/logs", StringComparison.OrdinalIgnoreCase);

    if (stil)
    {
        await next(context);
        return;
    }

    var start = System.Diagnostics.Stopwatch.GetTimestamp();
    await next(context);
    var duur = System.Diagnostics.Stopwatch.GetElapsedTime(start);

    activity.Add(
        "broker",
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{context.Request.Method} {path} -> {context.Response.StatusCode} ({duur.TotalMilliseconds:0} ms)"));
});

app.MapGet("/health", () => Results.Ok());

// Alles na dit volgnummer. De afnemer onthoudt het hoogste dat hij zag en
// geeft dat de volgende keer mee, zodat hij niets dubbel binnenkrijgt.
app.MapGet("/logs", (ActivityLog log, long since = 0) => Results.Ok(log.Since(since)));

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
