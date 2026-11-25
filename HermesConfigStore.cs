using System.Diagnostics;
using System.Text.Json;
using Jabasoft.Base.AiBroker;
using Jabasoft.Base.Logging;
using YamlDotNet.RepresentationModel;

namespace Jabasoft.Broker;

/// <summary>
/// Leest en schrijft de instellingen van Hermes Agent die Jabasoft beheert,
/// rechtstreeks in Hermes' eigen config.yaml (in HERMES_HOME, standaard
/// %LOCALAPPDATA%\hermes).
///
/// Bewust ALLEEN de sleutels van Jabasoft: model, auxiliary.compression,
/// auxiliary.vision, fallback_providers en mcp_servers.jabasoft. Alles wat
/// Hermes zelf in het bestand zet blijft staan. Vóór elke wijziging gaat er
/// een kopie naar config.yaml.jabasoft-backup. Let op: commentaarregels in
/// config.yaml overleven het herschrijven niet (YAML-bibliotheek), de
/// instellingen zelf wel.
///
/// API-sleutels komen hier nooit langs: de broker kijkt alleen OF er een
/// sleutel of login staat (in .env of auth.json), niet wat die is.
/// </summary>
internal sealed class HermesConfigStore(IHttpClientFactory httpClients, AiSettingsStore aiSettings, ActivityLog log)
{
    private const string JabasoftMcpUrl = "http://localhost:5310/mcp";

    /// <summary>
    /// Waar Hermes zijn lokale modellen vraagt: bij de broker (HermesProxy),
    /// niet rechtstreeks bij LM Studio - zo loopt het door de wachtrij en
    /// laadt de broker het model met genoeg context.
    /// </summary>
    private const string BrokerV1Url = "http://localhost:5310/v1";

    private static readonly Lock Slot = new();

    private static string HermesMap =>
        Environment.GetEnvironmentVariable("HERMES_HOME") is { Length: > 0 } eigen
            ? eigen
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes");

    private static string ConfigPad => Path.Combine(HermesMap, "config.yaml");

    /// <summary>Het adres waar Hermes lokale modellen vraagt - de broker (zie <see cref="BrokerV1Url"/>).</summary>
    private static string LokaalServerUrl => BrokerV1Url;

    public async Task<HermesStatus> LeesAsync(CancellationToken ct)
    {
        var lokaleModellen = await LokaleModellenAsync(ct);

        if (!Directory.Exists(HermesMap))
        {
            return HermesStatus.Leeg with { HermesMap = HermesMap, LokaalServerUrl = LokaalServerUrl, LokaleModellen = lokaleModellen };
        }

        YamlMappingNode root;
        lock (Slot)
        {
            root = LaadRoot();
        }

        var model = Map(root, "model");
        var provider = Tekst(model, "provider");
        var compressie = Map(Map(root, "auxiliary"), "compression");
        var vision = Map(Map(root, "auxiliary"), "vision");

        // Een "custom" provider op de broker is voor de kaart gewoon LM Studio
        // (zo schrijft BewaarAsync het weg, zie daar).
        if (string.Equals(provider, "custom", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Tekst(model, "base_url").TrimEnd('/'), BrokerV1Url, StringComparison.OrdinalIgnoreCase))
        {
            provider = "lmstudio";
        }

        // Op Ollama staan de modellen in de broker-instelling (zie BewaarAsync); config.yaml houdt de LM Studio-namen.
        var ollama = aiSettings.Current.Provider == AiProvider.Ollama ? aiSettings.Current.Ollama : null;
        var hoofdmodel = Tekst(model, "default") is { Length: > 0 } standaard ? standaard : Tekst(model, "model");
        var lokaalModel = Tekst(compressie, "model") is { Length: > 0 } bijtaak ? bijtaak : Tekst(vision, "model");

        return new HermesStatus(
            Geinstalleerd: true,
            GatewayDraait: GatewayDraait(),
            HermesMap: HermesMap,
            Provider: provider,
            Model: ollama is not null && IsLokaal(provider) ? ollama.HermesModel : hoofdmodel,
            Sleutel: SleutelStatus(provider),
            // Bij een cloud-hoofdmodel staat het lokale model bij compression; bij een lokaal hoofdmodel alleen
            // bij vision (zie BewaarAsync).
            LokaalModel: ollama is not null ? ollama.HermesLokaalModel : lokaalModel,
            LokaalServerUrl: LokaalServerUrl,
            JabasoftMcp: Map(root, "mcp_servers")?.Children.ContainsKey(new YamlScalarNode("jabasoft")) == true,
            LokaleModellen: lokaleModellen,
            MaxContext: GrensUit(model));
    }

    /// <summary>
    /// De grens tot waar de context van het lokale hoofdmodel mag meegroeien: de context_length in config.yaml
    /// (die geven we Hermes ook door, zodat Hermes pas daar gaat samenvatten). Ontbreekt hij, of is hij niet
    /// groter dan de begincontext, dan de standaard - vóór het meegroeien schreef de broker hier altijd vast
    /// 65.536, en dat moet niet als grens blijven hangen. Voor HermesProxy, bij elke vraag - daarom onthouden zolang het
    /// bestand niet verandert.
    /// </summary>
    public static int IngesteldeMaxContext()
    {
        try
        {
            var gewijzigd = File.Exists(ConfigPad) ? File.GetLastWriteTimeUtc(ConfigPad) : DateTime.MinValue;
            lock (Slot)
            {
                if (_grensCache is { } cache && cache.Gewijzigd == gewijzigd)
                {
                    return cache.Grens;
                }

                var grens = GrensUit(Map(LaadRoot(), "model"));
                _grensCache = (gewijzigd, grens);
                return grens;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or YamlDotNet.Core.YamlException)
        {
            return HermesStatus.StandaardMaxContext;
        }
    }

    private static (DateTime Gewijzigd, int Grens)? _grensCache;

    /// <summary>
    /// Het hoofdmodel en het beeldmodel zoals ze NU in config.yaml staan - alleen als het hoofdmodel via de broker
    /// loopt (anders null). Voor HermesProxy, dat er bij elke vraag van Hermes het ingestelde model mee kiest, zodat
    /// wisselen op de Hermes-kaart meteen werkt. Onthouden zolang het bestand niet verandert.
    /// </summary>
    public static (string Hoofd, string Beeld)? IngesteldeModellen()
    {
        try
        {
            var gewijzigd = File.Exists(ConfigPad) ? File.GetLastWriteTimeUtc(ConfigPad) : DateTime.MinValue;
            lock (Slot)
            {
                if (_modelCache is { } cache && cache.Gewijzigd == gewijzigd)
                {
                    return cache.Modellen;
                }

                var root = LaadRoot();
                var model = Map(root, "model");
                var vision = Map(Map(root, "auxiliary"), "vision");
                (string, string)? modellen = null;

                if (string.Equals(Tekst(model, "provider"), "custom", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Tekst(model, "base_url").TrimEnd('/'), BrokerV1Url, StringComparison.OrdinalIgnoreCase))
                {
                    var beeld = string.Equals(Tekst(vision, "base_url").TrimEnd('/'), BrokerV1Url, StringComparison.OrdinalIgnoreCase)
                        ? Tekst(vision, "model")
                        : string.Empty;
                    modellen = (Tekst(model, "default") is { Length: > 0 } standaard ? standaard : Tekst(model, "model"), beeld);
                }

                _modelCache = (gewijzigd, modellen);
                return modellen;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or YamlDotNet.Core.YamlException)
        {
            return null;
        }
    }

    private static (DateTime Gewijzigd, (string Hoofd, string Beeld)? Modellen)? _modelCache;

    /// <summary>
    /// Zet alleen het hoofdmodel in config.yaml op <paramref name="naam"/> - als het hoofdmodel via de broker loopt.
    /// Voor een keuze die in Hermes zelf gemaakt is (zie HermesProxy.VolgHermesKeuze), zodat de Hermes-kaart die volgt.
    /// De rest van het bestand blijft zoals het is. False als er niets veranderd is.
    /// </summary>
    public static bool ZetHoofdmodel(string naam)
    {
        try
        {
            lock (Slot)
            {
                var root = LaadRoot();
                var model = Map(root, "model");
                if (model is null ||
                    !string.Equals(Tekst(model, "provider"), "custom", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Tekst(model, "base_url").TrimEnd('/'), BrokerV1Url, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Tekst(model, "default"), naam, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                Zet(model, "default", naam);
                SchrijfRoot(root);
                _modelCache = null;
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or YamlDotNet.Core.YamlException)
        {
            return false;
        }
    }

    private static int GrensUit(YamlMappingNode? model) =>
        int.TryParse(Tekst(model, "context_length"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var waarde) &&
        waarde > HermesProxy.BeginContext
            ? waarde
            : HermesStatus.StandaardMaxContext;

    public async Task<HermesStatus> BewaarAsync(HermesInstelling nieuw, CancellationToken ct)
    {
        if (!Directory.Exists(HermesMap))
        {
            return HermesStatus.Leeg with { HermesMap = HermesMap, Melding = "Hermes Agent is niet geïnstalleerd (geen map " + HermesMap + ")." };
        }

        // Op Ollama: de lokale modellen gaan in de broker-instelling, en in config.yaml blijven de LM Studio-namen
        // staan - die heeft de broker weer nodig als de chatserver terug naar LM Studio gaat. Hermes stuurt die
        // naam mee; de broker zet er het Ollama-model voor in de plaats (HermesProxy.KiesModel).
        var opOllama = aiSettings.Current.Provider == AiProvider.Ollama;
        if (opOllama)
        {
            var huidig = aiSettings.Current;
            aiSettings.Save(huidig.With(AiProvider.Ollama, huidig.Ollama with
            {
                HermesModel = IsLokaal(nieuw.Provider) ? nieuw.Model.Trim() : huidig.Ollama.HermesModel,
                HermesLokaalModel = nieuw.LokaalModel.Trim(),
            }));
        }

        try
        {
            lock (Slot)
            {
                var root = LaadRoot();

                // --- Hoofdmodel -------------------------------------------
                var model = ZorgVoorMap(root, "model");
                var helemaalLokaal = IsLokaal(nieuw.Provider);
                var vorigeLokaal = Tekst(Map(Map(root, "auxiliary"), "compression"), "model") is { Length: > 0 } bijtaak
                    ? bijtaak
                    : Tekst(Map(Map(root, "auxiliary"), "vision"), "model");

                // Op Ollama: een lokaal hoofdmodel houdt in config.yaml zijn LM Studio-naam (zie hierboven); een
                // cloudmodel is van geen van beide servers en gaat er gewoon in.
                if (!opOllama || !helemaalLokaal)
                {
                    Zet(model, "default", nieuw.Model.Trim());
                }
                else if (!string.Equals(Tekst(model, "base_url").TrimEnd('/'), BrokerV1Url, StringComparison.OrdinalIgnoreCase))
                {
                    // Was het hoofdmodel een cloudmodel, dan staat er geen LM Studio-naam: neem het chatmodel van
                    // LM Studio, zodat terugwisselen naar LM Studio meteen werkt.
                    Zet(model, "default", aiSettings.Current.LmStudio.ChatModel);
                }

                if (helemaalLokaal)
                {
                    // Lokaal = via de broker. Als "custom" (een OpenAI-
                    // compatibel adres) en NIET als "lmstudio": die laatste
                    // praat ook met LM Studio's eigen API (modellen laden,
                    // context opvragen) en zou zo alsnog om de broker heen
                    // gaan. De context staat erbij omdat Hermes hem bij een
                    // custom adres niet zelf kan opvragen - het is de grens
                    // tot waar de broker het model laat meegroeien (zie
                    // HermesProxy), en daar gaat Hermes ook van uit voor
                    // het samenvatten. 0 = de bestaande grens houden.
                    var grens = nieuw.MaxContext > 0 ? nieuw.MaxContext : GrensUit(model);
                    grens = grens > HermesProxy.BeginContext ? grens : HermesStatus.StandaardMaxContext;
                    Zet(model, "provider", "custom");
                    Zet(model, "base_url", BrokerV1Url);
                    Zet(model, "context_length", grens.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    Zet(model, "provider", nieuw.Provider.Trim());
                    model.Children.Remove(new YamlScalarNode("base_url"));
                    model.Children.Remove(new YamlScalarNode("context_length"));
                }

                // --- Lokaal model: bijtaken en reserve --------------------
                // Bij een cloud-hoofdmodel: samenvatten, beelden en reserve.
                // Is alles al lokaal, dan doet het hoofdmodel het samenvatten
                // zelf (auto) en is er geen reserve: die zou hetzelfde model
                // op dezelfde plek zijn. Wel kan dan een apart model de
                // BEELDEN lezen - voor een hoofdmodel dat dat niet kan (zoals
                // gpt-oss). De broker wisselt de twee dan om de beurt.
                var auxiliary = ZorgVoorMap(root, "auxiliary");
                var lokaal = nieuw.LokaalModel.Trim();

                // Op Ollama de LM Studio-naam laten staan (zie boven). Was er nog geen, dan de Ollama-naam: wel of
                // geen lokaal model moet Hermes hoe dan ook weten, en de broker vervangt de naam toch.
                if (opOllama && lokaal.Length > 0 && vorigeLokaal.Length > 0)
                {
                    lokaal = vorigeLokaal;
                }

                if (helemaalLokaal)
                {
                    auxiliary.Children.Remove(new YamlScalarNode("compression"));
                    root.Children.Remove(new YamlScalarNode("fallback_providers"));

                    if (lokaal.Length > 0 && KanBeelden(lokaal))
                    {
                        var vision = ZorgVoorMap(auxiliary, "vision");
                        Zet(vision, "provider", "custom");
                        Zet(vision, "model", lokaal);
                        Zet(vision, "base_url", LokaalServerUrl);
                    }
                    else
                    {
                        auxiliary.Children.Remove(new YamlScalarNode("vision"));
                    }
                }
                else if (lokaal.Length == 0)
                {
                    auxiliary.Children.Remove(new YamlScalarNode("compression"));
                    auxiliary.Children.Remove(new YamlScalarNode("vision"));
                    root.Children.Remove(new YamlScalarNode("fallback_providers"));
                }
                else
                {
                    // provider ERBIJ: een base_url zonder provider (en zonder
                    // api_key) negeert Hermes en valt terug op "auto" - het
                    // hoofdmodel (zie _resolve_task_provider_model in Hermes'
                    // agent/auxiliary_client.py).
                    var compressie = ZorgVoorMap(auxiliary, "compression");
                    Zet(compressie, "provider", "custom");
                    Zet(compressie, "model", lokaal);
                    Zet(compressie, "base_url", LokaalServerUrl);

                    // Vision alleen als het model ook beelden kan lezen;
                    // anders weg, dan doet Hermes' hoofdmodel het (auto).
                    if (KanBeelden(lokaal))
                    {
                        var vision = ZorgVoorMap(auxiliary, "vision");
                        Zet(vision, "provider", "custom");
                        Zet(vision, "model", lokaal);
                        Zet(vision, "base_url", LokaalServerUrl);
                    }
                    else
                    {
                        auxiliary.Children.Remove(new YamlScalarNode("vision"));
                    }

                    root.Children[new YamlScalarNode("fallback_providers")] = new YamlSequenceNode(
                        new YamlMappingNode(
                            new YamlScalarNode("provider"), new YamlScalarNode("custom"),
                            new YamlScalarNode("model"), new YamlScalarNode(lokaal),
                            new YamlScalarNode("base_url"), new YamlScalarNode(LokaalServerUrl)));
                }

                if (auxiliary.Children.Count == 0)
                {
                    root.Children.Remove(new YamlScalarNode("auxiliary"));
                }

                // --- Jabasoft-tools (MCP) ---------------------------------
                var mcp = ZorgVoorMap(root, "mcp_servers");
                if (nieuw.JabasoftMcp)
                {
                    // 600 s: zo lang mag "teken" duren (BeeldTools.MaxTekenTijd) - het laden van een beeldmodel
                    // alleen al kan langer duren dan de 120 s die hier eerst stond, en dan gaf Hermes het op.
                    mcp.Children[new YamlScalarNode("jabasoft")] = new YamlMappingNode(
                        new YamlScalarNode("url"), new YamlScalarNode(JabasoftMcpUrl),
                        new YamlScalarNode("timeout"), new YamlScalarNode("600"),
                        new YamlScalarNode("connect_timeout"), new YamlScalarNode("15"));
                }
                else
                {
                    mcp.Children.Remove(new YamlScalarNode("jabasoft"));
                }

                if (mcp.Children.Count == 0)
                {
                    root.Children.Remove(new YamlScalarNode("mcp_servers"));
                }

                SchrijfRoot(root);
            }

            log.Add("broker", $"Hermes ingesteld: {nieuw.Provider} '{nieuw.Model}', lokaal '{nieuw.LokaalModel}', Jabasoft-tools {(nieuw.JabasoftMcp ? "aan" : "uit")}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or YamlDotNet.Core.YamlException)
        {
            log.Add("broker", $"Hermes-instelling niet bewaard: {ex.Message}");
            return (await LeesAsync(ct)) with { Melding = "Niet bewaard: " + ex.Message };
        }

        return await LeesAsync(ct);
    }

    // ------------------------------------------------------------------
    // Bestand

    private static YamlMappingNode LaadRoot()
    {
        if (!File.Exists(ConfigPad))
        {
            return new YamlMappingNode();
        }

        var stream = new YamlStream();
        using (var reader = new StreamReader(ConfigPad))
        {
            stream.Load(reader);
        }

        return stream.Documents.Count > 0 && stream.Documents[0].RootNode is YamlMappingNode root
            ? root
            : new YamlMappingNode();
    }

    private static void SchrijfRoot(YamlMappingNode root)
    {
        if (File.Exists(ConfigPad))
        {
            File.Copy(ConfigPad, ConfigPad + ".jabasoft-backup", overwrite: true);
        }

        // Eerst naast het echte bestand, dan in één keer vervangen: Hermes
        // leest het bestand mogelijk net op dat moment.
        var tijdelijk = ConfigPad + ".tmp";
        using (var writer = new StreamWriter(tijdelijk, append: false, new System.Text.UTF8Encoding(false)))
        {
            new YamlStream(new YamlDocument(root)).Save(writer, assignAnchors: false);
        }

        File.Move(tijdelijk, ConfigPad, overwrite: true);
    }

    // ------------------------------------------------------------------
    // Status

    private static bool GatewayDraait()
    {
        var pidBestand = Path.Combine(HermesMap, "gateway.pid");
        if (!File.Exists(pidBestand))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(pidBestand));
            if (!doc.RootElement.TryGetProperty("pid", out var pid))
            {
                return false;
            }

            using var proces = Process.GetProcessById(pid.GetInt32());

            // Een oud gateway.pid kan wijzen naar een nummer dat Windows inmiddels
            // aan een ander proces gaf (bijvoorbeeld een systeemproces, waar de
            // broker niet in mag kijken). Alleen een Python-proces kan de gateway zijn.
            if (!proces.ProcessName.Contains("python", StringComparison.OrdinalIgnoreCase) &&
                !proces.ProcessName.Contains("hermes", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !proces.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException or IOException or FormatException
                                       or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Staat er een sleutel of login voor deze provider? Kijkt in .env naar
    /// de bekende variabele en in auth.json naar een login - alleen of er
    /// iets STAAT, nooit wat.
    /// </summary>
    private static HermesSleutelStatus SleutelStatus(string provider)
    {
        var p = provider.Trim().ToLowerInvariant();
        if (p.Length == 0)
        {
            return HermesSleutelStatus.Onbekend;
        }

        if (IsLokaal(p))
        {
            return HermesSleutelStatus.NietNodig;
        }

        string[] variabelen = p switch
        {
            "anthropic" => ["ANTHROPIC_API_KEY", "ANTHROPIC_TOKEN"],
            "openrouter" => ["OPENROUTER_API_KEY"],
            "openai" => ["OPENAI_API_KEY"],
            "nous" => [],
            _ => [],
        };

        if (variabelen.Any(naam => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(naam))) ||
            EnvHeeft(variabelen) ||
            AuthHeeft(p))
        {
            return HermesSleutelStatus.Ingesteld;
        }

        return p is "anthropic" or "openrouter" or "openai" or "nous"
            ? HermesSleutelStatus.Ontbreekt
            : HermesSleutelStatus.Onbekend;
    }

    private static bool EnvHeeft(string[] variabelen)
    {
        var env = Path.Combine(HermesMap, ".env");
        if (variabelen.Length == 0 || !File.Exists(env))
        {
            return false;
        }

        foreach (var regel in File.ReadLines(env))
        {
            var deel = regel.Split('=', 2);
            if (deel.Length == 2 &&
                variabelen.Contains(deel[0].Trim(), StringComparer.OrdinalIgnoreCase) &&
                deel[1].Trim().Trim('"', '\'').Length > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool AuthHeeft(string provider)
    {
        var auth = Path.Combine(HermesMap, "auth.json");
        if (!File.Exists(auth))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(auth));
            var root = doc.RootElement;

            if (root.TryGetProperty("providers", out var providers) &&
                providers.ValueKind == JsonValueKind.Object &&
                providers.TryGetProperty(provider, out var login) &&
                login.ValueKind == JsonValueKind.Object &&
                login.EnumerateObject().Any())
            {
                return true;
            }

            return root.TryGetProperty("credential_pool", out var pool) &&
                   pool.ValueKind == JsonValueKind.Object &&
                   pool.TryGetProperty(provider, out var lijst) &&
                   lijst.ValueKind == JsonValueKind.Array &&
                   lijst.GetArrayLength() > 0;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// De modellen op LM Studio met hun grootste context - via LM Studio's
    /// eigen /api/v0/models, want die vertelt ook max_context_length (de
    /// gewone /v1/models niet). Embeddingmodellen vallen af: die kunnen niet
    /// chatten. Ook voor de Goose-kaart (zie GooseConfigStore).
    /// </summary>
    public async Task<IReadOnlyList<HermesLokaalModel>> LokaleModellenAsync(CancellationToken ct)
    {
        // Staat de chatserver op Ollama, dan de modellen van Ollama: daar praten Goose en Hermes dan mee.
        if (aiSettings.Current.Provider == AiProvider.Ollama)
        {
            return [.. (await OllamaContext.ModellenAsync(aiSettings.Current.Ollama.Url, ct))
                .Select(m => new HermesLokaalModel(m.Naam, m.Max, m.KanBeelden))
                .OrderBy(m => m.Naam, StringComparer.OrdinalIgnoreCase)];
        }

        try
        {
            var http = httpClients.CreateClient("catalog");
            using var response = await http.GetAsync(aiSettings.Current.LmStudio.Url.TrimEnd('/') + "/api/v0/models", ct);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("data", out var data))
            {
                return [];
            }

            return data.EnumerateArray()
                .Where(m => !(m.TryGetProperty("type", out var type) && type.GetString() == "embeddings"))
                .Select(m => new HermesLokaalModel(
                    m.GetProperty("id").GetString() ?? string.Empty,
                    m.TryGetProperty("max_context_length", out var max) && max.TryGetInt32(out var waarde) ? waarde : 0,
                    m.TryGetProperty("type", out var soort) && soort.GetString() == "vlm"))
                .Where(m => m.Naam.Length > 0)
                .OrderBy(m => m.Naam, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return [];
        }
    }

    // ------------------------------------------------------------------
    // Hulp

    private static bool IsLokaal(string provider) =>
        provider.Trim().ToLowerInvariant() is "lmstudio" or "ollama" or "custom";

    private static bool KanBeelden(string model) =>
        model.Contains("-vl", StringComparison.OrdinalIgnoreCase) ||
        model.Contains("vision", StringComparison.OrdinalIgnoreCase) ||
        model.Contains("gemma-3", StringComparison.OrdinalIgnoreCase) ||
        model.Contains("gemma3", StringComparison.OrdinalIgnoreCase);

    private static YamlMappingNode? Map(YamlMappingNode? ouder, string sleutel) =>
        ouder is not null && ouder.Children.TryGetValue(new YamlScalarNode(sleutel), out var kind) ? kind as YamlMappingNode : null;

    private static string Tekst(YamlMappingNode? ouder, string sleutel) =>
        ouder is not null && ouder.Children.TryGetValue(new YamlScalarNode(sleutel), out var kind) && kind is YamlScalarNode scalar
            ? scalar.Value ?? string.Empty
            : string.Empty;

    private static YamlMappingNode ZorgVoorMap(YamlMappingNode ouder, string sleutel)
    {
        if (Map(ouder, sleutel) is { } bestaand)
        {
            return bestaand;
        }

        var nieuw = new YamlMappingNode();
        ouder.Children[new YamlScalarNode(sleutel)] = nieuw;
        return nieuw;
    }

    private static void Zet(YamlMappingNode map, string sleutel, string waarde) =>
        map.Children[new YamlScalarNode(sleutel)] = new YamlScalarNode(waarde);
}
