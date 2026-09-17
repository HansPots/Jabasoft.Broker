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

// De ene AI-instelling van deze machine: welke serversoort, waar, en welke
// twee modellen. Staat hier en niet in de apps, zodat er maar een plek is
// waar hij vandaan komt - zie AiSettingsStore.
builder.Services.AddSingleton<AiSettingsStore>();

// Het tokenverbruik van elke aanroep, in de gedeelde tabel
// JabasoftBase.dbo.TokenUsageEntries. Ook hier bij de broker, en om
// dezelfde reden: hij ziet alles langskomen, dus de apps hoeven zelf geen
// databaseverbinding te hebben.
builder.Services.AddSingleton<TokenUsageStore>();

builder.Services.AddSingleton<ProviderClient>();
builder.Services.AddSingleton<RequestGate>();

var app = builder.Build();

var activity = app.Services.GetRequiredService<ActivityLog>();
activity.Add("broker", "Broker gestart");

// Elke aanroep komt in het overzicht, behalve wat er AUTOMATISCH en
// herhaald gevraagd wordt: /health, /logs en de modeltotalen achter het
// tokenblok in de header. Die komen elke paar seconden langs en zouden al
// het andere wegdrukken - juist het werk dat je wél wilt zien.
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    var stil = path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/logs", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/api/usage/models", StringComparison.OrdinalIgnoreCase);

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

// --- De AI-instelling ----------------------------------------------------
// Een voor de hele machine, en dus voor elke applicatie. De apps lezen 'm
// hier; het instellingenscherm van Jabasoft schrijft 'm hier.

app.MapGet("/api/settings", (AiSettingsStore store) => Results.Ok(store.Current));

app.MapPut("/api/settings", (AiSettings settings, AiSettingsStore store, ActivityLog log) =>
{
    var opgeslagen = store.Save(settings);
    log.Add("broker", $"AI-instelling: {opgeslagen.Provider} op {opgeslagen.ActiveServerUrl}, chat '{opgeslagen.Active.ChatModel}', embedding '{opgeslagen.Active.EmbedModel}'");
    return Results.Ok(opgeslagen);
});

// De modellen die AANWEZIG zijn op de server die nu ingesteld staat. Dit is
// de controle waar de applicaties op leunen: zij hoeven niet te weten welke
// soort server het is of waar die draait, ze krijgen de lijst en leggen die
// naast wat er ingesteld is. Loopt via dezelfde poort als /api/models, dus
// ook met de korte cache eromheen.
app.MapGet("/api/settings/models", async (AiSettingsStore store, RequestGate gate, CancellationToken ct) =>
{
    var settings = store.Current;
    var result = await gate.GetModelsAsync(settings.Provider, settings.ActiveServerUrl, ct);
    return Results.Ok(result);
});

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

app.MapPost("/api/chat", async (ChatRequest request, ProviderClient client, RequestGate gate, TokenUsageStore usage, CancellationToken ct) =>
{
    var result = await gate.RunGatedAsync(
        request.Provider,
        request.ServerUrl,
        () => client.ChatAsync(request.Provider, request.ServerUrl, request.Model, request.Messages, request.Temperature, ct),
        ct);

    // Na het antwoord, niet ervoor: pas dan weten we wat het gekost heeft.
    // Mislukt het wegschrijven, dan merkt de aanroeper daar niets van - zie
    // TokenUsageStore.
    if (result.Success)
    {
        await usage.RecordAsync(request.Application, request.Model, result.PromptTokens, result.CompletionTokens, ct);
    }

    return Results.Ok(result);
});

app.MapPost("/api/embed", async (EmbedRequest request, ProviderClient client, RequestGate gate, TokenUsageStore usage, CancellationToken ct) =>
{
    var result = await gate.RunGatedAsync(
        request.Provider,
        request.ServerUrl,
        () => client.EmbedAsync(request.Provider, request.ServerUrl, request.Model, request.Text, ct),
        ct);

    // Een embedding kent geen antwoordtokens - alles zit aan de promptkant.
    if (result.Success)
    {
        await usage.RecordAsync(request.Application, request.Model, result.TokensUsed, 0, ct);
    }

    return Results.Ok(result);
});

// --- Tokenverbruik -------------------------------------------------------
// Staat de database er? Hier leunt de gezondheidscontrole van elke
// applicatie op, dus dit antwoord zegt ook WAT er mis is.
app.MapGet("/api/usage/status", async (TokenUsageStore usage, CancellationToken ct) =>
    Results.Ok(await usage.GetStatusAsync(ct)));

// De weektotalen voor het overzicht, nieuwste eerst.
app.MapGet("/api/usage/weeks", async (TokenUsageStore usage, CancellationToken ct) =>
    Results.Ok(await usage.GetWeeksAsync(ct)));

// Het totaal per model, zwaarste eerst - voor het tokenblok in de header.
app.MapGet("/api/usage/models", async (TokenUsageStore usage, CancellationToken ct) =>
    Results.Ok(await usage.GetModelsAsync(ct)));

// De losse regels van één week - pas opgehaald als je die week openklapt.
app.MapGet("/api/usage/entries", async (string week, TokenUsageStore usage, CancellationToken ct) =>
    Results.Ok(await usage.GetEntriesAsync(week, ct)));

app.Run();
