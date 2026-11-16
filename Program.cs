using System.Text.Json;
using System.Text.Json.Serialization;
using Jabasoft.Ai.Data;
using Jabasoft.Base.AiBroker;
using Jabasoft.Base.Logging;
using Jabasoft.Broker;
using Microsoft.EntityFrameworkCore;

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

// Voor het doorgeefluik van Hermes (HermesProxy): ruim, want een lokaal
// model kan bij een lange agentstap even bezig zijn voor het eerste stukje
// binnen is - en daarna loopt de stroom buiten deze tijdslimiet om.
builder.Services.AddHttpClient("hermes", c => c.Timeout = TimeSpan.FromMinutes(15));

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
builder.Services.AddSingleton<ProviderStream>();
builder.Services.AddSingleton<RequestGate>();

// De boekhouding van alle vragen aan de modellen: wat loopt, wat wacht, wat
// net klaar is. Elke applicatie toont hem in haar footer (GET /api/queue) en
// kan er een vraag mee annuleren - zie AiWachtrij.
builder.Services.AddSingleton<AiWachtrij>();

// De AI-database: JabasoftAi, een eigen database naast JabasoftBase en
// JabasoftStylebook. AddDbContext (scoped) en niet AddSingleton: een
// DbContext is niet thread-safe en Minimal API maakt toch al een scope
// per binnenkomend verzoek, dus dat past precies.
builder.Services.AddDbContext<AiDbContext>(options => options.UseSqlServer(
    builder.Configuration.GetConnectionString("JabasoftAi")
    ?? throw new InvalidOperationException("ConnectionStrings:JabasoftAi ontbreekt in appsettings.json.")));

builder.Services.AddScoped<SearchIndexStore>();

// Hermes Agent: de instellingen die Jabasoft in Hermes' config.yaml beheert
// (hoofdmodel, lokaal model, Jabasoft-tools) - zie HermesConfigStore.
builder.Services.AddSingleton<HermesConfigStore>();

// Goose (Block): het model dat Jabasoft in Goose' config beheert - zie GooseConfigStore.
builder.Services.AddSingleton<GooseConfigStore>();

// MCP: dezelfde broker, maar als gereedschap voor AI-agents (Claude Code,
// Hermes) op /mcp. Voorlopig alleen tools die LEZEN - zie McpTools.cs.
// Stateless: elke aanroep staat op zichzelf, er is geen sessie die de
// broker moet onthouden (net als bij de rest van de API).
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<McpTools>()
    .WithTools<BeeldTools>();

// Voor de beeldservice van ImageFunn (BeeldTools): korte aanroepen - het
// wachten op een tekening gebeurt door telkens de status op te vragen.
builder.Services.AddHttpClient("beeld", c => c.Timeout = TimeSpan.FromSeconds(30));

var app = builder.Build();

// De database (en de tabel erin) meteen op orde bij het opstarten - net
// als Stylebook.Playground bij zijn eigen database doet. Bestaat de
// database nog niet, dan wordt hij hier aangemaakt; bestaat hij al, dan
// lopen alleen de migraties bij die nog niet toegepast zijn.
using (var opstartScope = app.Services.CreateScope())
{
    opstartScope.ServiceProvider.GetRequiredService<AiDbContext>().Database.Migrate();
}

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
        || path.StartsWith("/api/usage/models", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/queue", StringComparison.OrdinalIgnoreCase);

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

// De vorm waarin de stukjes van een streamend antwoord over de lijn gaan:
// dezelfde als van elk ander antwoord van de broker, zodat de client er
// niets aparts voor hoeft te doen.
var StreamJson = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    Converters = { new JsonStringEnumConverter() },
};

app.MapGet("/health", () => Results.Ok());

// Alles na dit volgnummer. De afnemer onthoudt het hoogste dat hij zag en
// geeft dat de volgende keer mee, zodat hij niets dubbel binnenkrijgt.
app.MapGet("/logs", (ActivityLog log, long since = 0) => Results.Ok(log.Since(since)));

// --- De AI-instelling ----------------------------------------------------
// Een voor de hele machine, en dus voor elke applicatie. De apps lezen 'm
// hier; het instellingenscherm van Jabasoft schrijft 'm hier.

app.MapGet("/api/settings", (AiSettingsStore store) => Results.Ok(store.Current));

app.MapPut("/api/settings", (AiSettings settings, AiSettingsStore store, ActivityLog log, IServiceScopeFactory scopes) =>
{
    var vorige = store.Current;
    var opgeslagen = store.Save(settings);
    log.Add("broker", $"AI-instelling: {opgeslagen.Provider} op {opgeslagen.ActiveServerUrl}, chat '{opgeslagen.Active.ChatModel}', embedding '{opgeslagen.Active.EmbedModel}'");

    // Wijzigt het embeddingmodel (of de server erachter), dan is elke tot
    // nu toe geïndexeerde vector niet meer vergelijkbaar met een vraag die
    // met het NIEUWE model geëmbed wordt - die moeten dus allemaal opnieuw.
    // Op de achtergrond en met een eigen scope: dit antwoord mag niet
    // wachten tot alle projecten herindexeerd zijn, en de scope van dit
    // verzoek is straks weg voordat dat klaar is.
    if (!string.IsNullOrWhiteSpace(opgeslagen.Active.EmbedModel) &&
        (opgeslagen.Active.EmbedModel != vorige.Active.EmbedModel || opgeslagen.ActiveServerUrl != vorige.ActiveServerUrl))
    {
        var provider = opgeslagen.Provider;
        var serverUrl = opgeslagen.ActiveServerUrl;
        var model = opgeslagen.Active.EmbedModel;

        _ = Task.Run(async () =>
        {
            using var scope = scopes.CreateScope();
            var index = scope.ServiceProvider.GetRequiredService<SearchIndexStore>();
            await index.ReindexAllAsync(provider, serverUrl, model, CancellationToken.None);
        });
    }

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

app.MapPost("/api/chat", async (ChatRequest request, ProviderClient client, RequestGate gate, TokenUsageStore usage, AiWachtrij wachtrij, CancellationToken ct) =>
{
    var vraag = wachtrij.Aanmelden(request.Application, request.Onderdeel, request.Model, "chat", ct);
    ChatResult result;
    string? afrondFout = "Afgebroken.";

    try
    {
        result = await gate.RunGatedAsync(
            request.Provider,
            request.ServerUrl,
            request.Model,
            () => client.ChatAsync(request.Provider, request.ServerUrl, request.Model, request.Messages, request.Temperature, vraag.Token),
            vraag.Token,
            () => wachtrij.Begonnen(vraag));
        afrondFout = result.Success ? null : result.ErrorMessage ?? "Mislukt.";
    }
    catch (OperationCanceledException) when (AiWachtrij.IsGeannuleerd(vraag))
    {
        result = new ChatResult(false, string.Empty, "Geannuleerd.");
    }
    finally
    {
        wachtrij.Afronden(vraag, afrondFout);
    }

    // Na het antwoord, niet ervoor: pas dan weten we wat het gekost heeft.
    // Mislukt het wegschrijven, dan merkt de aanroeper daar niets van - zie
    // TokenUsageStore.
    if (result.Success)
    {
        await usage.RecordAsync(request.Application, request.Model, result.PromptTokens, result.CompletionTokens, request.Onderdeel, ct);
    }

    return Results.Ok(result);
});

// Hetzelfde gesprek, maar streamend: het antwoord komt stukje bij beetje
// terug (één JSON-object per regel), zodat een applicatie de tekst kan
// laten aangroeien en een teller kan laten meelopen. Het wegschrijven van
// het verbruik gebeurt hier net zo goed - aan het eind, als de server
// verteld heeft wat het gekost heeft.
app.MapPost("/api/chat/stream", async (ChatRequest request, ProviderStream stream, RequestGate gate, TokenUsageStore usage, ActivityLog log, AiSettingsStore settings, AiWachtrij wachtrij, HttpContext context, CancellationToken ct) =>
{
    // In de wachtrij: vanaf nu zichtbaar in de footer van elke applicatie, en
    // te annuleren. Het token van de vraag gaat ook af als de aanvrager
    // ophangt, dus het vervangt ct voor alles behalve het terugschrijven.
    var vraag = wachtrij.Aanmelden(request.Application, request.Onderdeel, request.Model, "chat", ct);

    context.Response.ContentType = "application/x-ndjson";

    // Niets bufferen: een stukje dat in een buffer blijft hangen tot het
    // antwoord af is, is precies wat streamen NIET is.
    var buffering = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
    buffering?.DisableBuffering();

    long promptTokens = 0;
    long completionTokens = 0;
    string? fout = null;

    // Staat er bij dit model een maximale denktijd ingesteld (Jabasoft, AI-
    // instellingen), dan breekt de broker het antwoord af zolang het model
    // nog AAN HET DENKEN is (thinking-stukjes, nog geen echte tekst) en die
    // tijd verstreken is - zo blijft een klein model niet minutenlang malen.
    // Zodra er echte tekst binnenkomt, telt de limiet niet meer mee: dat is
    // gewoon de normale ChatTimeoutSeconden.
    var maxDenktijd = settings.MaxDenktijd(request.Provider, request.Model);
    using var denktijdBewaking = maxDenktijd > 0 ? new CancellationTokenSource() : null;
    using var gekoppeld = denktijdBewaking is null
        ? null
        : CancellationTokenSource.CreateLinkedTokenSource(vraag.Token, denktijdBewaking.Token);
    var aanroepToken = gekoppeld?.Token ?? vraag.Token;

    var begin = DateTime.UtcNow;
    var echteTekstGezien = false;
    string? teLangNagedacht = null;

    try
    {
        await foreach (var stukje in gate.RunGatedStreamAsync(
            request.Provider,
            request.ServerUrl,
            request.Model,
            () => stream.ChatAsync(request.Provider, request.ServerUrl, request.Model, request.Messages, request.Temperature, aanroepToken),
            aanroepToken,
            () => wachtrij.Begonnen(vraag)))
        {
            wachtrij.Voortgang(vraag, stukje);

            if (!echteTekstGezien && !string.IsNullOrEmpty(stukje.Text))
            {
                echteTekstGezien = true;
            }

            if (!echteTekstGezien && maxDenktijd > 0 && (DateTime.UtcNow - begin).TotalSeconds > maxDenktijd)
            {
                teLangNagedacht = $"{request.Model} dacht langer na dan de ingestelde {maxDenktijd} s - antwoord afgebroken.";
                denktijdBewaking!.Cancel();
                break;
            }

            if (stukje.Done)
            {
                promptTokens = stukje.PromptTokens;
                completionTokens = stukje.CompletionTokens;
                fout = stukje.ErrorMessage;
            }

            await context.Response.WriteAsync(JsonSerializer.Serialize(stukje, StreamJson) + "\n", ct);
            await context.Response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException) when (teLangNagedacht is not null)
    {
        // Verwacht: dit is onze EIGEN afbreking hierboven, geen echte annulering.
    }
    catch (OperationCanceledException) when (AiWachtrij.IsGeannuleerd(vraag) && !ct.IsCancellationRequested)
    {
        // Iemand klikte op Annuleren. De aanvrager hangt nog aan de lijn, dus
        // die krijgt netjes een slotstukje met de reden.
        fout = "Geannuleerd.";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new ChatStreamChunk(Done: true, ErrorMessage: fout), StreamJson) + "\n", ct);
        await context.Response.Body.FlushAsync(ct);
    }
    finally
    {
        wachtrij.Afronden(vraag, fout ?? teLangNagedacht ?? (ct.IsCancellationRequested ? "Afgebroken." : null));
    }

    if (teLangNagedacht is not null)
    {
        fout = teLangNagedacht;
        await context.Response.WriteAsync(JsonSerializer.Serialize(new ChatStreamChunk(Done: true, ErrorMessage: teLangNagedacht), StreamJson) + "\n", ct);
        await context.Response.Body.FlushAsync(ct);
    }

    if (fout is null && (promptTokens > 0 || completionTokens > 0))
    {
        await usage.RecordAsync(request.Application, request.Model, promptTokens, completionTokens, request.Onderdeel, ct);
    }

    log.Add("broker", fout is null
        ? $"Chat (stromend) met {request.Model}: {promptTokens + completionTokens} tokens"
        : $"Chat (stromend) met {request.Model} mislukt: {fout}");
});

app.MapPost("/api/embed", async (EmbedRequest request, ProviderClient client, RequestGate gate, TokenUsageStore usage, AiWachtrij wachtrij, CancellationToken ct) =>
{
    var vraag = wachtrij.Aanmelden(request.Application, request.Onderdeel, request.Model, "embedding", ct);
    EmbedResult result;
    string? afrondFout = "Afgebroken.";

    try
    {
        result = await gate.RunGatedAsync(
            request.Provider,
            request.ServerUrl,
            request.Model,
            () => client.EmbedAsync(request.Provider, request.ServerUrl, request.Model, request.Text, vraag.Token),
            vraag.Token,
            () => wachtrij.Begonnen(vraag));
        afrondFout = result.Success ? null : result.ErrorMessage ?? "Mislukt.";
    }
    catch (OperationCanceledException) when (AiWachtrij.IsGeannuleerd(vraag))
    {
        result = new EmbedResult(false, [], "Geannuleerd.");
    }
    finally
    {
        wachtrij.Afronden(vraag, afrondFout);
    }

    // Een embedding kent geen antwoordtokens - alles zit aan de promptkant.
    if (result.Success)
    {
        await usage.RecordAsync(request.Application, request.Model, result.TokensUsed, 0, request.Onderdeel, ct);
    }

    return Results.Ok(result);
});

// --- Wachtrij -------------------------------------------------------------
// Wat er aan vragen loopt, wacht en net klaar is - voor de footer van elke
// applicatie. Annuleren haalt een wachtende vraag uit de rij of breekt een
// lopende af; 404 als hij er niet (meer) is.
app.MapGet("/api/queue", (AiWachtrij wachtrij) => Results.Ok(wachtrij.Lijst()));

app.MapPost("/api/queue/{id:long}/annuleren", (long id, AiWachtrij wachtrij, ActivityLog log) =>
{
    if (!wachtrij.Annuleer(id))
    {
        return Results.NotFound();
    }

    log.Add("broker", $"Wachtrij: vraag {id} geannuleerd");
    return Results.Ok();
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

// Het verbruik per onderdeel van een applicatie over de laatste N dagen.
app.MapGet("/api/usage/onderdelen", async (int dagen, TokenUsageStore usage, CancellationToken ct) =>
    Results.Ok(await usage.GetOnderdelenAsync(dagen, ct)));

// De losse regels van één week - pas opgehaald als je die week openklapt.
app.MapGet("/api/usage/entries", async (string week, TokenUsageStore usage, CancellationToken ct) =>
    Results.Ok(await usage.GetEntriesAsync(week, ct)));

// --- Zoekindex (semantisch zoeken) ---------------------------------------
// Keyword search heeft de broker niet nodig - dat gaat rechtstreeks over de
// bestanden op schijf. Deze twee zijn voor de vector-kant: indexeren en
// erin zoeken. Zie SearchIndexStore voor waarom het vector als tekst
// bewaard wordt in plaats van met het native VECTOR-type.
app.MapPost("/api/search/index", async (SearchIndexRequest request, SearchIndexStore index, CancellationToken ct) =>
    Results.Ok(await index.IndexAsync(request, ct)));

app.MapPost("/api/search/semantic", async (SemanticSearchRequest request, SearchIndexStore index, CancellationToken ct) =>
    Results.Ok(await index.SearchAsync(request, ct)));

// --- Hermes Agent ----------------------------------------------------------
// Voor de Hermes-kaart in Jabasoft: status lezen en de instellingen van
// Jabasoft (hoofdmodel, lokaal model, Jabasoft-tools) wijzigen. Sleutels
// komen hier nooit langs - alleen OF ze er zijn.
app.MapGet("/api/hermes", async (HermesConfigStore hermes, CancellationToken ct) =>
    Results.Ok(await hermes.LeesAsync(ct)));

app.MapPut("/api/hermes", async (HermesInstelling instelling, HermesConfigStore hermes, CancellationToken ct) =>
    Results.Ok(await hermes.BewaarAsync(instelling, ct)));

// --- Goose -----------------------------------------------------------------
// Voor de Goose-kaart in Jabasoft: welk Jabasoft-model Goose gebruikt.
app.MapGet("/api/goose", async (GooseConfigStore goose, CancellationToken ct) =>
    Results.Ok(await goose.LeesAsync(ct)));

app.MapPut("/api/goose", async (GooseInstelling instelling, GooseConfigStore goose, CancellationToken ct) =>
    Results.Ok(await goose.BewaarAsync(instelling, ct)));

// Het doorgeefluik naar LM Studio voor Hermes (/v1/...), via dezelfde
// wachtrij als de apps - zie HermesProxy.
HermesProxy.Map(app);

// --- MCP ------------------------------------------------------------------
// Het endpoint voor AI-agents. Alleen op localhost, net als de rest: van
// buitenaf (telefoon) loopt het via de gateway van Hermes.
app.MapMcp("/mcp");

app.Run();
