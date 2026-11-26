using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jabasoft.Base.AiBroker;
using Jabasoft.Base.Logging;

namespace Jabasoft.Broker;

/// <summary>
/// Een OpenAI-compatibel doorgeefluik (/v1/models, /v1/chat/completions) voor
/// Hermes Agent, zodat Hermes LM Studio deelt met de Jabasoft-apps in plaats
/// van er buiten de broker om bij te gaan:
///
/// - elke vraag gaat door dezelfde wachtrij (RequestGate + AiWachtrij):
///   zichtbaar in de footer en te annuleren, en meerdere tegelijk alleen als
///   ze hetzelfde geladen model gebruiken en dat model dat aankan;
/// - eerst laadt de broker het model zelf (zie LmStudioContext): met
///   <see cref="BeginContext"/> tokens - wat Hermes minimaal eist - en
///   groter zodra het gesprek dat nodig heeft, in stappen van
///   <see cref="Stap"/>, tot de grens op de Hermes-kaart (context_length in
///   Hermes' config.yaml) of het maximum van het model. Laat je LM Studio
///   dat zelf doen, dan krijgt het model zijn eigen, te kleine standaard;
/// - het verbruik telt mee onder de applicatie die de vraag stuurt: "OpenClaw"
///   of "Hermes" (zie <see cref="AanvragerVan"/>). Ook OpenClaw gebruikt dit
///   adres; de naam HermesProxy is gebleven van toen het alleen voor Hermes was.
///
/// De vraag en het antwoord gaan ONGEWIJZIGD door (behalve dat een stromende
/// vraag om het verbruik aan het eind vraagt): tools, afbeeldingen en
/// streaming werken dus precies zoals rechtstreeks bij LM Studio.
/// </summary>
internal static class HermesProxy
{
    /// <summary>De context waarmee het model voor Hermes in elk geval geladen wordt - Hermes weigert minder dan 64.000.</summary>
    public const int BeginContext = 65_536;

    /// <summary>
    /// Goose stelt die eis niet: dat begint kleiner, scheelt videogeheugen bij een kort gesprek. Daarboven gelden
    /// dezelfde stappen (<see cref="Stap"/>).
    /// </summary>
    public const int GooseBeginContext = 32_768;

    /// <summary>De kleinste context voor deze aanvrager: Goose 32K, de rest (Hermes) 64K.</summary>
    private static int BeginVoor(string aanvrager) =>
        string.Equals(aanvrager, Goose, StringComparison.Ordinal) ? GooseBeginContext : BeginContext;

    /// <summary>Hoeveel context een model minstens moet aankunnen om voor deze aanvrager te mogen.</summary>
    private static int MinimumVoor(string aanvrager) =>
        string.Equals(aanvrager, Goose, StringComparison.Ordinal) ? GooseBeginContext : HermesStatus.MinimaleContext;

    /// <summary>
    /// Hoeveel de context per keer groeit. Elke groeistap is een herlaadbeurt (10-30 s, en LM Studio leest het
    /// gesprek daarna opnieuw in), dus liever een paar grote stappen dan veel kleine.
    /// </summary>
    public const int Stap = 32_768;

    /// <summary>Ruimte voor het antwoord als de vraag zelf geen max_tokens noemt.</summary>
    private const int AntwoordReserve = 8_192;

    /// <summary>
    /// Tekens per token voor de schatting. Bewust aan de krappe kant (echte tekst is eerder 3,5-4): te weinig
    /// geschat geeft een fout van LM Studio midden in een gesprek, te veel alleen een iets grotere context.
    /// </summary>
    private const double TekensPerToken = 3.0;

    /// <summary>Een afbeelding telt voor ongeveer zoveel tokens.</summary>
    private const int TokensPerAfbeelding = 1_000;

    /// <summary>
    /// Per model de kleinste context waarbij het laden mislukte (videogeheugen vol), en wanneer. Zo probeert de
    /// broker dat niet bij elke vraag opnieuw - wel weer na <see cref="TeGrootVergeten"/>, want vaak was het
    /// geheugen maar even bezet (bijvoorbeeld door de beeldservice van ImageFunn).
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Context, DateTimeOffset Wanneer)> TeGroot =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan TeGrootVergeten = TimeSpan.FromMinutes(10);

    /// <summary>Kopregel waarmee een programma zegt wie het is (OpenClaw stuurt hem mee via zijn providerinstellingen).</summary>
    private const string AanvragerKop = "X-Jabasoft-App";

    /// <summary>
    /// Welk programma deze vraag stuurt, voor de wachtrij, het verbruik en het logboek. Eerst de eigen kopregel
    /// (<see cref="AanvragerKop"/>), dan de User-Agent; wie zich niet bekendmaakt, telt als "Hermes", zoals voorheen.
    /// </summary>
    private static string AanvragerVan(HttpContext context)
    {
        // Goose heeft een eigen adres (/goose/v1) - zie Map.
        if (context.Items[AanvragerItem] is string vast)
        {
            return vast;
        }

        var opgegeven = context.Request.Headers[AanvragerKop].ToString().Trim();
        if (opgegeven.Length > 0)
        {
            return opgegeven.Length > 40 ? opgegeven[..40] : opgegeven;
        }

        var agent = context.Request.Headers.UserAgent.ToString();
        return agent.Contains("openclaw", StringComparison.OrdinalIgnoreCase) ? "OpenClaw" : "Hermes";
    }

    // --- Geschat inleespercentage -------------------------------------------
    // LM Studio's OpenAI-API (die Hermes spreekt) meldt NIET hoe ver het
    // inlezen van de vraag is - zijn eigen API, die de apps gebruiken, wel.
    // Daarom schatten we het: hoeveel NIEUWE tekst er in te lezen is, gedeeld
    // door hoe snel LM Studio dat bij eerdere Hermes-vragen deed. "Nieuw",
    // omdat LM Studio het begin van een gesprek onthoudt: alleen wat sinds
    // de vorige vraag is bijgekomen kost echt tijd. Alles in TEKENS (niet
    // tokens): de schatting en de meting gebruiken dezelfde maat, dus een
    // tekens-per-token-omrekening is niet nodig.

    private static readonly Lock SchattingSlot = new();

    /// <summary>De vorige vraag (zonder afbeeldingen) - om te zien hoeveel er nieuw is.</summary>
    private static string _vorigeVraag = string.Empty;

    /// <summary>Gemeten inleessnelheid in tekens per seconde. Begint voorzichtig; elke vraag stelt bij.</summary>
    private static double _tekensPerSeconde = 6_000;

    /// <summary>Een afbeelding telt voor zoveel tekens tekst (ongeveer 1000 tokens).</summary>
    private const int TekensPerAfbeelding = 3_500;

    private static readonly System.Text.RegularExpressions.Regex AfbeeldingData = new(
        "data:image/[^\"]+",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Per aanvrager de laatst gelogde modelwissel - zie ChatAsync.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> LaatsteWissel = new(StringComparer.Ordinal);

    private static readonly Lock HermesKeuzeSlot = new();

    /// <summary>
    /// Waar de broker bijhoudt welk hoofdmodel Hermes noemt, sinds wanneer, en wanneer de Hermes-kaart voor het laatst
    /// is opgeslagen - zodat <see cref="VolgHermesKeuze"/> ook na een herstart van de broker weet wie het laatst wisselde.
    /// </summary>
    private static readonly string HermesKeuzePad = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jabasoft", "Broker", "hermes-keuze.json");

    private sealed record HermesKeuze(string? Model, DateTimeOffset Sinds, DateTimeOffset KaartOpgeslagen);

    private static HermesKeuze _hermesKeuze = LeesHermesKeuze();

    private static HermesKeuze LeesHermesKeuze()
    {
        try
        {
            if (File.Exists(HermesKeuzePad) && JsonSerializer.Deserialize<HermesKeuze>(File.ReadAllText(HermesKeuzePad)) is { } keuze)
            {
                return keuze;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return new HermesKeuze(null, DateTimeOffset.MinValue, DateTimeOffset.MinValue);
    }

    private static void BewaarHermesKeuze(HermesKeuze keuze)
    {
        _hermesKeuze = keuze;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HermesKeuzePad)!);
            File.WriteAllText(HermesKeuzePad, JsonSerializer.Serialize(keuze));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Niet kunnen bewaren: in het werkgeheugen klopt het nog; alleen na een herstart is het weg.
        }
    }

    /// <summary>De Hermes-kaart is opgeslagen (HermesConfigStore.BewaarAsync): vanaf nu wint de kaart, tot je in Hermes zelf weer wisselt.</summary>
    public static void HermesKaartOpgeslagen()
    {
        lock (HermesKeuzeSlot)
        {
            BewaarHermesKeuze(_hermesKeuze with { KaartOpgeslagen = DateTimeOffset.UtcNow });
        }
    }

    /// <summary>
    /// Wie het laatst wisselt, wint. Hermes stuurt bij elke vraag het model dat hij gebruikt: het model uit zijn config bij
    /// het opstarten, of wat je daarna in Hermes zelf koos. De broker onthoudt sinds wanneer Hermes zijn huidige model
    /// noemt, en wanneer de Hermes-kaart voor het laatst is opgeslagen:
    ///
    ///  - Hermes noemt dit model pas SINDS de laatste keer dat de kaart werd opgeslagen: je hebt in Hermes gewisseld. Dat
    ///    model wordt het hoofdmodel in config.yaml (dus ook op de Hermes-kaart), zodat <see cref="KiesModel"/> het gebruikt.
    ///  - Hermes noemde dit model al vóór de kaart werd opgeslagen: er is daarna op de kaart gewisseld, en de kaart wint.
    ///
    /// Beide tijden staan in een bestand (<see cref="HermesKeuzePad"/>), dus dit geldt ook direct na een herstart van de
    /// broker. Niet voor beeldvragen of het beeldmodel (een bijtaak, geen wissel), niet voor modellen die Hermes niet kan
    /// gebruiken en niet op Ollama (daar staan de Hermes-modellen in de broker-instelling).
    /// </summary>
    private static void VolgHermesKeuze(string gevraagd, string vraagTekst, IReadOnlyCollection<string> bruikbaar, ActivityLog log)
    {
        if (HermesConfigStore.IngesteldeModellen() is not { } ingesteld)
        {
            return;
        }

        var (hoofd, beeld) = ingesteld;
        if (AfbeeldingData.IsMatch(vraagTekst) || string.Equals(gevraagd, beeld, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (HermesKeuzeSlot)
        {
            if (!string.Equals(_hermesKeuze.Model, gevraagd, StringComparison.OrdinalIgnoreCase))
            {
                BewaarHermesKeuze(_hermesKeuze with { Model = gevraagd, Sinds = DateTimeOffset.UtcNow });
            }

            if (string.Equals(gevraagd, hoofd, StringComparison.OrdinalIgnoreCase) ||
                (bruikbaar.Count > 0 && !bruikbaar.Contains(gevraagd, StringComparer.OrdinalIgnoreCase)) ||
                _hermesKeuze.Sinds <= _hermesKeuze.KaartOpgeslagen)
            {
                return;
            }
        }

        if (HermesConfigStore.ZetHoofdmodel(gevraagd))
        {
            log.Add("broker", $"Hermes koos zelf {gevraagd} (was {hoofd}): de broker volgt die keuze en zet hem op de Hermes-kaart");
        }
    }

    /// <summary>Waar de route de aanvrager vastlegt (zie <see cref="AanvragerVan"/>).</summary>
    private const string AanvragerItem = "Jabasoft.Aanvrager";

    /// <summary>De naam waaronder Goose' vragen in de wachtrij, het verbruik en het logboek staan.</summary>
    public const string Goose = "Goose";

    public static void Map(WebApplication app)
    {
        app.MapGet("/v1/models", ModellenAsync);
        app.MapPost("/v1/chat/completions", ChatAsync);

        // Goose op een eigen adres: zo weet de broker zeker dat een vraag van
        // Goose komt, en kan hij er het model van de Goose-kaart in zetten
        // (zie KiesModel). Verder precies hetzelfde doorgeefluik.
        var goose = app.MapGroup("/goose").AddEndpointFilter(async (filter, next) =>
        {
            filter.HttpContext.Items[AanvragerItem] = Goose;
            return await next(filter);
        });
        goose.MapGet("/v1/models", ModellenAsync);
        goose.MapPost("/v1/chat/completions", ChatAsync);
    }

    /// <summary>
    /// Het model dat Jabasoft NU voor deze aanvrager heeft ingesteld, zodat
    /// wisselen op de Hermes- of Goose-kaart meteen werkt - zonder Hermes of
    /// Goose te herstarten, die het model alleen bij het opstarten uit hun
    /// config lezen en tot dan de oude naam blijven sturen.
    ///
    ///  - Goose: het model van de Jabasoft-provider in Goose' config.
    ///  - Hermes (alleen als zijn hoofdmodel via de broker loopt): een vraag
    ///    met een afbeelding gaat naar het ingestelde beeldmodel als dat er is,
    ///    al het andere naar het hoofdmodel.
    ///  - Iedereen anders (OpenClaw, eigen kopregel): ongewijzigd.
    ///
    /// Leeg of niet te lezen = het gevraagde model houden.
    ///
    /// Staat de chatserver op Ollama, dan komen de modellen uit de Ollama-instelling (AiServerSettings.GooseModel,
    /// HermesModel, HermesLokaalModel), met het chatmodel van Ollama als terugval: de config van Goose en Hermes
    /// houdt de LM Studio-namen, en die bestaan bij Ollama niet.
    /// </summary>
    private static string KiesModel(string aanvrager, string gevraagd, string vraagTekst, AiSettings instelling)
    {
        if (instelling.Provider == AiProvider.Ollama)
        {
            var ollama = instelling.Ollama;
            static string Eerste(params string[] namen) => namen.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? string.Empty;

            if (string.Equals(aanvrager, Goose, StringComparison.Ordinal))
            {
                return Eerste(ollama.GooseModel, ollama.ChatModel, gevraagd);
            }

            if (string.Equals(aanvrager, "Hermes", StringComparison.Ordinal))
            {
                // Een beeldvraag naar het lokale model als dat er is; loopt Hermes' hoofdmodel niet via de broker
                // (cloud), dan is alles wat hier binnenkomt een bijtaak - ook voor het lokale model.
                if ((AfbeeldingData.IsMatch(vraagTekst) || HermesConfigStore.IngesteldeModellen() is null) && ollama.HermesLokaalModel.Length > 0)
                {
                    return ollama.HermesLokaalModel;
                }

                return Eerste(ollama.HermesModel, ollama.ChatModel, gevraagd);
            }

            return gevraagd;
        }

        if (string.Equals(aanvrager, Goose, StringComparison.Ordinal))
        {
            return GooseConfigStore.IngesteldModel() is { Length: > 0 } gooseModel ? gooseModel : gevraagd;
        }

        if (string.Equals(aanvrager, "Hermes", StringComparison.Ordinal) && HermesConfigStore.IngesteldeModellen() is var (hoofd, beeld))
        {
            if (beeld.Length > 0 && AfbeeldingData.IsMatch(vraagTekst))
            {
                return beeld;
            }

            return hoofd.Length > 0 ? hoofd : gevraagd;
        }

        return gevraagd;
    }

    /// <summary>Het adres van de chatserver die nu gekozen is (LM Studio of Ollama).</summary>
    private static string ServerUrl(AiSettings instelling) => instelling.ActiveServerUrl.TrimEnd('/');

    private static string ServerNaam(AiProvider provider) => provider == AiProvider.Ollama ? "Ollama" : "LM Studio";

    /// <summary>Tot waar de context mag meegroeien: de grens van de Goose-kaart voor Goose, anders die van de Hermes-kaart.</summary>
    private static int GrensVoor(HttpContext context) =>
        string.Equals(AanvragerVan(context), Goose, StringComparison.Ordinal)
            ? GooseConfigStore.IngesteldeMaxContext()
            : HermesConfigStore.IngesteldeMaxContext();

    /// <summary>
    /// Alleen de modellen waar Hermes mee kan werken: chatmodellen (geen
    /// embeddings, geen beeldgeneratoren) die minstens 64.000 tokens context
    /// aankunnen. Anders biedt Hermes' modelkeuze ook een embeddingmodel aan,
    /// en dat geeft bij de eerste vraag een fout. Elk model draagt de context
    /// tot waar de broker het laat meegroeien, zodat Hermes die niet hoeft te
    /// raden. Modellen die in de modeltabel op "niet tonen" staan vallen weg,
    /// behalve het model dat nu voor deze aanvrager ingesteld staat. Het
    /// weigeren van een vraag (ChatAsync) kijkt hier bewust niet naar: een
    /// verborgen model dat toch gevraagd wordt, werkt gewoon.
    /// </summary>
    private static async Task ModellenAsync(HttpContext context, IHttpClientFactory httpClients, AiSettingsStore settings)
    {
        var instelling = settings.Current;
        var aanvrager = AanvragerVan(context);
        var ingesteld = KiesModel(aanvrager, string.Empty, string.Empty, instelling);
        var bruikbaar = (await BruikbareModellenMetMaxAsync(httpClients, settings, context.RequestAborted))
            .Where(model => model.Max >= MinimumVoor(aanvrager))
            .Where(model => !instelling.Active.IsVerborgen(model.Naam) || string.Equals(model.Naam, ingesteld, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var grens = GrensVoor(context);

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            @object = "list",
            data = bruikbaar.Select(model => new
            {
                id = model.Naam,
                @object = "model",
                owned_by = instelling.Provider == AiProvider.Ollama ? "ollama" : "lmstudio",
                context_length = Math.Min(grens, model.Max),
            }),
        }));
    }

    /// <summary>
    /// De chatmodellen op de gekozen chatserver met de grootste context die elk aankan. Nog niet gefilterd op het
    /// minimum van de aanvrager (zie <see cref="MinimumVoor"/>) - dat doen de aanroepers.
    /// </summary>
    private static async Task<List<(string Naam, int Max)>> BruikbareModellenMetMaxAsync(IHttpClientFactory httpClients, AiSettingsStore settings, CancellationToken ct)
    {
        // Korte cache: dit loopt bij ELKE vraag van Hermes, en de lijst
        // verandert zelden. Per server: na een wissel geldt de oude lijst niet.
        var instelling = settings.Current;
        var sleutel = instelling.Provider + " " + ServerUrl(instelling);
        if (_bruikbaarCache is { } cache && cache.Sleutel == sleutel && DateTimeOffset.UtcNow - cache.Opgehaald < TimeSpan.FromSeconds(30))
        {
            return cache.Modellen;
        }

        var modellen = instelling.Provider == AiProvider.Ollama
            ? [.. (await OllamaContext.ModellenAsync(ServerUrl(instelling), ct)).Select(m => (m.Naam, m.Max))]
            : await HaalBruikbareModellenAsync(httpClients, instelling, ct);

        if (modellen.Count > 0)
        {
            _bruikbaarCache = (sleutel, DateTimeOffset.UtcNow, modellen);
        }

        return modellen;
    }

    private static (string Sleutel, DateTimeOffset Opgehaald, List<(string Naam, int Max)> Modellen)? _bruikbaarCache;

    private static async Task<List<(string Naam, int Max)>> HaalBruikbareModellenAsync(IHttpClientFactory httpClients, AiSettings instelling, CancellationToken ct)
    {
        try
        {
            var http = httpClients.CreateClient("catalog");
            using var antwoord = await http.GetAsync(ServerUrl(instelling) + "/api/v0/models", ct);
            if (!antwoord.IsSuccessStatusCode)
            {
                return [];
            }

            using var doc = JsonDocument.Parse(await antwoord.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("data", out var data))
            {
                return [];
            }

            return [.. data.EnumerateArray()
                .Where(m => m.TryGetProperty("type", out var type) && type.GetString() is "llm" or "vlm")
                .Select(m => (
                    Naam: m.GetProperty("id").GetString() ?? string.Empty,
                    Max: m.TryGetProperty("max_context_length", out var max) && max.TryGetInt32(out var waarde) ? waarde : 0))
                .Where(m => m.Max > 0 && m.Naam.Length > 0)];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return [];
        }
    }

    private static async Task ChatAsync(
        HttpContext context,
        IHttpClientFactory httpClients,
        AiSettingsStore settings,
        RequestGate gate,
        AiWachtrij wachtrij,
        TokenUsageStore usage,
        ActivityLog log)
    {
        string tekst;
        using (var lezer = new StreamReader(context.Request.Body, Encoding.UTF8))
        {
            tekst = await lezer.ReadToEndAsync(context.RequestAborted);
        }

        if (JsonNode.Parse(tekst) is not JsonObject vraagJson || vraagJson["model"]?.GetValue<string>() is not { Length: > 0 } model)
        {
            await FoutAsync(context, 400, "Geen geldige vraag: 'model' ontbreekt.");
            return;
        }

        // Welke modellen bruikbaar zijn: nodig om een wissel in Hermes zelf te herkennen (VolgHermesKeuze) en om een
        // onbruikbaar model hieronder te weigeren.
        var instelling = settings.Current;
        var metMax = await BruikbareModellenMetMaxAsync(httpClients, settings, context.RequestAborted);
        var minimum = MinimumVoor(AanvragerVan(context));
        var bruikbaar = metMax.Where(m => m.Max >= minimum).Select(m => m.Naam).ToList();

        // Wie het laatst wisselt wint: in Hermes zelf gekozen gaat naar de Hermes-kaart (zie VolgHermesKeuze).
        if (instelling.Provider != AiProvider.Ollama && string.Equals(AanvragerVan(context), "Hermes", StringComparison.Ordinal))
        {
            VolgHermesKeuze(model, tekst, bruikbaar, log);
        }

        // Live wisselen: het model van de Hermes- of Goose-kaart in plaats van
        // wat het programma bij het opstarten las (zie KiesModel).
        var ingesteld = KiesModel(AanvragerVan(context), model, tekst, instelling);
        if (!string.Equals(ingesteld, model, StringComparison.OrdinalIgnoreCase))
        {
            // Eén regel per wissel, niet bij elke vraag: tot een herstart stuurt het programma steeds de oude naam.
            var wissel = model + " → " + ingesteld;
            if (LaatsteWissel.TryGetValue(AanvragerVan(context), out var vorige) is false || vorige != wissel)
            {
                LaatsteWissel[AanvragerVan(context)] = wissel;
                log.Add("broker", $"{AanvragerVan(context)} vroeg {model}, broker gebruikt het ingestelde {ingesteld}");
            }

            vraagJson["model"] = ingesteld;
            model = ingesteld;
        }

        // Een model waar Hermes niet mee kan werken (embedding, te weinig
        // context) meteen weigeren, met een melding die zegt wat er mis is -
        // anders laadt de broker het voor niets en zegt LM Studio iets vaags.
        if (bruikbaar.Count > 0 && !bruikbaar.Contains(model, StringComparer.OrdinalIgnoreCase))
        {
            var melding = $"'{model}' is op {ServerNaam(instelling.Provider)} geen chatmodel met minstens {minimum:N0} tokens context en kan niet voor {AanvragerVan(context)} gebruikt worden. Kies in Jabasoft (Instellingen > {AanvragerVan(context)}) een van: {string.Join(", ", bruikbaar)}.";
            log.Add("broker", $"{AanvragerVan(context)} via broker geweigerd: " + melding);
            await FoutAsync(context, 400, melding);
            return;
        }

        var stromend = vraagJson["stream"] is JsonValue s && s.TryGetValue<bool>(out var aan) && aan;
        if (stromend)
        {
            // Zonder dit zegt een stromend antwoord niet wat het gekost heeft.
            var opties = vraagJson["stream_options"] as JsonObject ?? new JsonObject();
            opties["include_usage"] = true;
            vraagJson["stream_options"] = opties;
        }

        // Tot hoever de context mag groeien: de grens van de Goose-kaart voor
        // Goose, anders die van de Hermes-kaart, maar nooit meer dan het model
        // zelf aankan. Onbekend model (lijst leeg, LM Studio even niet
        // bereikbaar): alleen de begincontext.
        var modelMax = metMax.FirstOrDefault(m => string.Equals(m.Naam, model, StringComparison.OrdinalIgnoreCase)).Max;
        var aanvrager = AanvragerVan(context);
        var begin = BeginVoor(aanvrager);
        var maxContext = modelMax > 0 ? Math.Min(GrensVoor(context), modelMax) : begin;

        var provider = instelling.Provider;
        var serverUrl = ServerUrl(instelling);
        var vraag = wachtrij.Aanmelden(aanvrager, aanvrager, model, "chat", context.RequestAborted);
        long promptTokens = 0;
        long completionTokens = 0;
        string? fout = "Afgebroken.";

        try
        {
            fout = await gate.RunGatedAsync(
                provider,
                serverUrl,
                model,
                async () =>
                {
                    var (laadfout, doel) = await LaadMetGenoegContextAsync(provider, serverUrl, model, vraagJson.ToJsonString(), vraagJson, begin, maxContext, aanvrager, log, vraag.Token);
                    if (laadfout is not null)
                    {
                        await FoutAsync(context, 503, laadfout);
                        return laadfout;
                    }

                    // Bij Ollama de variant met de juiste context (zie OllamaContext); bij LM Studio het model zelf.
                    vraagJson["model"] = doel;
                    var verzonden = vraagJson.ToJsonString();

                    using var verzoek = new HttpRequestMessage(HttpMethod.Post, serverUrl + "/v1/chat/completions")
                    {
                        Content = new StringContent(verzonden, Encoding.UTF8, "application/json"),
                    };

                    // Vanaf hier (model staat geladen) loopt de inleesklok.
                    using var inlezen = StartInleesSchatting(verzonden, vraag, wachtrij);

                    var http = httpClients.CreateClient("hermes");
                    using var antwoord = await http.SendAsync(verzoek, HttpCompletionOption.ResponseHeadersRead, vraag.Token);

                    context.Response.StatusCode = (int)antwoord.StatusCode;
                    context.Response.ContentType = antwoord.Content.Headers.ContentType?.ToString() ?? "application/json";
                    context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();

                    await using var bron = await antwoord.Content.ReadAsStreamAsync(vraag.Token);

                    if (stromend)
                    {
                        // Regel voor regel doorgeven (server-sent events) en
                        // meteen doorspoelen; onderweg het verbruik oppikken.
                        using var regels = new StreamReader(bron, Encoding.UTF8);
                        while (await regels.ReadLineAsync(vraag.Token) is { } regel)
                        {
                            await context.Response.WriteAsync(regel + "\n", vraag.Token);
                            await context.Response.Body.FlushAsync(vraag.Token);

                            if (regel.StartsWith("data: {", StringComparison.Ordinal))
                            {
                                // Eerste stukje antwoord = inlezen klaar.
                                inlezen.Klaar();
                                wachtrij.Voortgang(vraag, new ChatStreamChunk(Text: "·"));
                                LeesVerbruik(regel[6..], ref promptTokens, ref completionTokens);
                            }
                        }
                    }
                    else
                    {
                        using var lezer = new StreamReader(bron, Encoding.UTF8);
                        var inhoud = await lezer.ReadToEndAsync(vraag.Token);
                        await context.Response.WriteAsync(inhoud, vraag.Token);
                        LeesVerbruik(inhoud, ref promptTokens, ref completionTokens);
                    }

                    return antwoord.IsSuccessStatusCode ? null : $"{ServerNaam(provider)} gaf {(int)antwoord.StatusCode}.";
                },
                vraag.Token,
                () => wachtrij.Begonnen(vraag));
        }
        catch (OperationCanceledException) when (AiWachtrij.IsGeannuleerd(vraag))
        {
            fout = "Geannuleerd.";
        }
        catch (HttpRequestException ex)
        {
            fout = ConnectionErrorFormatter.Describe(ex, serverUrl);
            if (!context.Response.HasStarted)
            {
                await FoutAsync(context, 502, fout);
            }
        }
        finally
        {
            wachtrij.Afronden(vraag, fout);
        }

        if (fout is null && (promptTokens > 0 || completionTokens > 0))
        {
            await usage.RecordAsync(aanvrager, model, promptTokens, completionTokens, aanvrager, CancellationToken.None);
        }

        log.Add("broker", fout is null
            ? $"{aanvrager} via broker met {model}: {promptTokens + completionTokens} tokens"
            : $"{aanvrager} via broker met {model} mislukt: {fout}");
    }

    /// <summary>Per model de context waarmee het laatst geladen werd - om alleen een wijziging te loggen.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> LaatsteContext =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Zorgt dat het model geladen staat met genoeg context voor deze vraag: de geschatte lengte van het hele
    /// gesprek plus ruimte voor het antwoord, afgerond naar boven op een <see cref="Stap"/>, minstens
    /// <paramref name="begin"/> (Goose 32K, Hermes 64K) en hoogstens <paramref name="maxContext"/>.
    ///
    /// Staat het model al geladen met een stap die past en niet meer dan twee keer zo groot is als nodig, dan
    /// gebeurt er niets - binnen een gesprek groeit de context dus alleen. Begint er na een lang gesprek een nieuw,
    /// kort gesprek, dan gaat het model terug naar een kleinere stap (LmStudioContext.Past, OllamaContext).
    ///
    /// LM Studio laadt het model zelf met die context; bij Ollama wordt het een variant met die context, en die
    /// naam komt terug als Doel. Past het niet in het videogeheugen, dan een stap kleiner, tot de begincontext.
    /// Wordt het gesprek dan toch te lang, dan meldt de server dat zelf en vat Hermes/Goose het gesprek samen.
    /// Fout null = geladen.
    /// </summary>
    private static async Task<(string? Fout, string Doel)> LaadMetGenoegContextAsync(
        AiProvider provider, string serverUrl, string model, string vraagTekst, JsonObject vraagJson, int begin, int maxContext,
        string aanvrager, ActivityLog log, CancellationToken ct)
    {
        var geschat = GeschatteTokens(vraagTekst, vraagJson);
        var gewenst = Math.Clamp((int)Math.Ceiling(geschat / (double)Stap) * Stap, begin, Math.Max(begin, maxContext));

        if (TeGroot.TryGetValue(model, out var teGroot))
        {
            if (DateTimeOffset.UtcNow - teGroot.Wanneer > TeGrootVergeten)
            {
                TeGroot.TryRemove(model, out _);
            }
            else if (gewenst >= teGroot.Context)
            {
                gewenst = Math.Max(begin, teGroot.Context - Stap);
            }
        }

        while (true)
        {
            string? fout;
            var doel = model;

            if (provider == AiProvider.Ollama)
            {
                (doel, fout) = await OllamaContext.ZorgVoorAsync(serverUrl, model, gewenst, ct);
            }
            else
            {
                (_, fout) = await LmStudioContext.ZorgVoorAsync(model, gewenst, ct);
            }

            if (fout is null)
            {
                if (LaatsteContext.TryGetValue(model, out var vorige) ? vorige != gewenst : gewenst > begin)
                {
                    log.Add("broker", $"{aanvrager}: {model} met minstens {gewenst:N0} tokens context op {ServerNaam(provider)} (gesprek ongeveer {geschat:N0} tokens)");
                }

                LaatsteContext[model] = gewenst;
                return (null, doel);
            }

            // Alleen bij een vol videogeheugen heeft kleiner proberen zin.
            if (gewenst <= begin || !fout.Contains("videogeheugen", StringComparison.Ordinal))
            {
                return (fout, doel);
            }

            TeGroot[model] = (gewenst, DateTimeOffset.UtcNow);
            var kleiner = gewenst - Stap;
            log.Add("broker", $"{aanvrager}: {model} past niet met {gewenst:N0} tokens context in het videogeheugen, nu {kleiner:N0}");
            gewenst = kleiner;
        }
    }

    /// <summary>
    /// Hoeveel tokens context deze vraag ongeveer nodig heeft: het hele gesprek (met tools en systeemtekst, alles
    /// wat Hermes meestuurt), afbeeldingen apart geteld, plus ruimte voor het antwoord.
    /// </summary>
    private static int GeschatteTokens(string vraagTekst, JsonObject vraagJson)
    {
        var beelden = AfbeeldingData.Count(vraagTekst);
        var tekst = AfbeeldingData.Replace(vraagTekst, string.Empty).Length;

        var antwoord = AntwoordReserve;
        foreach (var sleutel in new[] { "max_tokens", "max_completion_tokens" })
        {
            if (vraagJson[sleutel] is JsonValue waarde && waarde.TryGetValue<int>(out var gevraagd) && gevraagd > 0)
            {
                antwoord = Math.Clamp(gevraagd, 1_024, 32_768);
            }
        }

        return (int)(tekst / TekensPerToken) + (beelden * TokensPerAfbeelding) + antwoord;
    }

    /// <summary>
    /// Begint de schatting voor één vraag: elke halve seconde een percentage
    /// in de wachtrij, tot <see cref="InleesSchatting.Klaar"/> (eerste stukje
    /// antwoord) of het einde. Nooit 100% zolang het niet echt klaar is: een
    /// schatting die "klaar" zegt terwijl er niets komt, is erger dan geen.
    /// </summary>
    private static InleesSchatting StartInleesSchatting(string vraagTekst, AiWachtrij.Vraag vraag, AiWachtrij wachtrij)
    {
        var zonderBeelden = AfbeeldingData.Replace(vraagTekst, string.Empty);
        var beelden = AfbeeldingData.Count(vraagTekst);

        double nieuw;
        double snelheid;
        lock (SchattingSlot)
        {
            var gedeeld = GedeeldBegin(_vorigeVraag, zonderBeelden);
            _vorigeVraag = zonderBeelden;
            nieuw = zonderBeelden.Length - gedeeld + (beelden * TekensPerAfbeelding);
            snelheid = _tekensPerSeconde;
        }

        return new InleesSchatting(Math.Max(1, nieuw), snelheid, vraag, wachtrij);
    }

    /// <summary>Hoeveel tekens twee vragen vanaf het begin gelijk hebben.</summary>
    private static int GedeeldBegin(string a, string b)
    {
        var lengte = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < lengte && a[i] == b[i])
        {
            i++;
        }

        return i;
    }

    /// <summary>Zie <see cref="StartInleesSchatting"/>.</summary>
    private sealed class InleesSchatting : IDisposable
    {
        /// <summary>Onder zoveel nieuwe tekens is het te snel om iets van te leren.</summary>
        private const double MinimaalOmTeLeren = 2_000;

        private readonly double _nieuweTekens;
        private readonly double _snelheid;
        private readonly System.Diagnostics.Stopwatch _klok = System.Diagnostics.Stopwatch.StartNew();
        private readonly CancellationTokenSource _stop = new();
        private int _klaar;

        public InleesSchatting(double nieuweTekens, double snelheid, AiWachtrij.Vraag vraag, AiWachtrij wachtrij)
        {
            _nieuweTekens = nieuweTekens;
            _snelheid = snelheid;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        var verwacht = _nieuweTekens / _snelheid;
                        var deel = Math.Min(0.95, _klok.Elapsed.TotalSeconds / Math.Max(0.1, verwacht));
                        wachtrij.Voortgang(vraag, new ChatStreamChunk(PromptProgress: deel));
                        await Task.Delay(500, _stop.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Klaar of afgebroken.
                }
            });
        }

        /// <summary>Het eerste stukje antwoord is er: stop de klok en leer van hoe lang het duurde.</summary>
        public void Klaar()
        {
            if (Interlocked.Exchange(ref _klaar, 1) == 1)
            {
                return;
            }

            _stop.Cancel();
            var seconden = _klok.Elapsed.TotalSeconds;

            if (_nieuweTekens >= MinimaalOmTeLeren && seconden > 0.2)
            {
                var gemeten = _nieuweTekens / seconden;
                lock (SchattingSlot)
                {
                    // Half oud, half nieuw: één uitschieter gooit het niet om.
                    _tekensPerSeconde = (_tekensPerSeconde + gemeten) / 2;
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _stop.Dispose();
        }
    }

    /// <summary>Pikt prompt_tokens/completion_tokens uit een antwoord (of een stukje ervan) als die erin staan.</summary>
    private static void LeesVerbruik(string json, ref long prompt, ref long antwoord)
    {
        if (!json.Contains("\"usage\"", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                if (u.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt64(out var pw))
                {
                    prompt = pw;
                }

                if (u.TryGetProperty("completion_tokens", out var c) && c.TryGetInt64(out var cw))
                {
                    antwoord = cw;
                }
            }
        }
        catch (JsonException)
        {
            // Geen volledig JSON-stuk: dan staat er geen verbruik in dat we kunnen lezen.
        }
    }

    /// <summary>Een fout in de vorm die een OpenAI-client verwacht, zodat Hermes hem netjes kan tonen.</summary>
    private static async Task FoutAsync(HttpContext context, int status, string melding)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = new { message = melding, type = "jabasoft_broker" } }));
    }
}
