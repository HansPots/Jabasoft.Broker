using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jabasoft.Base.AiBroker;
using Jabasoft.Base.Logging;
using YamlDotNet.RepresentationModel;

namespace Jabasoft.Broker;

/// <summary>
/// Leest en schrijft het model van Goose (Block) dat Jabasoft beheert, in
/// Goose' eigen config (%APPDATA%\Block\goose\config). Net als bij Hermes
/// bewust ALLEEN wat Jabasoft instelt:
///
///  - config.yaml: active_provider en providers.jabasoft (model, enabled,
///    configured). De rest (extensies, modus) blijft zoals Goose het zet.
///  - custom_providers\jabasoft.json: de modellijst die Goose bij de
///    Jabasoft-provider toont. Het gekozen model komt erbij als het er nog
///    niet in staat, anders kan Goose het niet kiezen.
///
/// Vóór elke wijziging gaat er een kopie naar *.jabasoft-backup.
/// </summary>
internal sealed class GooseConfigStore(HermesConfigStore hermes, AiSettingsStore aiSettings, ActivityLog log)
{
    /// <summary>De naam van de provider die naar de broker wijst (zie jabasoft.json).</summary>
    private const string Provider = "jabasoft";

    /// <summary>
    /// De grens als er nog niets ingesteld is: zo ver laat de broker een lokaal model voor Goose meegroeien
    /// (zie HermesProxy) - tenzij het model zelf minder aankan.
    /// </summary>
    private const int StandaardGrens = HermesStatus.StandaardMaxContext;

    private static readonly Lock Slot = new();

    private static string ConfigMap => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Block", "goose", "config");

    private static string ConfigPad => Path.Combine(ConfigMap, "config.yaml");

    private static string ProviderPad => Path.Combine(ConfigMap, "custom_providers", Provider + ".json");

    /// <summary>
    /// Het eigen adres van Goose bij de broker (zie HermesProxy.Map): zo weet de broker dat een vraag van Goose
    /// komt en zet hij er het model van de Goose-kaart in.
    /// </summary>
    private const string GooseUrl = "http://localhost:5310/goose/v1/chat/completions";

    /// <summary>Het gedeelde adres waar jabasoft.json eerst naar wees - wordt <see cref="GooseUrl"/>.</summary>
    private const string OudeUrl = "http://localhost:5310/v1/chat/completions";

    /// <summary>
    /// Het model van de Jabasoft-provider zoals het NU in Goose' config staat (leeg als een andere provider
    /// actief is). Voor HermesProxy, bij elke vraag van Goose - zo werkt wisselen op de Goose-kaart meteen, ook
    /// in een Goose die al draait. Onthouden zolang het bestand niet verandert.
    /// </summary>
    public static string IngesteldModel()
    {
        try
        {
            var gewijzigd = File.Exists(ConfigPad) ? File.GetLastWriteTimeUtc(ConfigPad) : DateTime.MinValue;
            lock (Slot)
            {
                if (_modelCache is { } cache && cache.Gewijzigd == gewijzigd)
                {
                    return cache.Model;
                }

                var model = string.Empty;
                if (File.Exists(ConfigPad))
                {
                    var root = LaadRoot();
                    if (string.Equals(Tekst(root, "active_provider"), Provider, StringComparison.OrdinalIgnoreCase))
                    {
                        model = Tekst(Map(Map(root, "providers"), Provider), "model");
                    }
                }

                _modelCache = (gewijzigd, model);
                return model;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or YamlDotNet.Core.YamlException)
        {
            return string.Empty;
        }
    }

    private static (DateTime Gewijzigd, string Model)? _modelCache;

    /// <summary>
    /// Tot hoever de context van Goose' model mag meegroeien: de context_limit van dat model in jabasoft.json
    /// (de grens op de Goose-kaart - Goose gaat daar zelf samenvatten). Ontbreekt die, dan de standaard. Voor
    /// HermesProxy, bij elke vraag van Goose - daarom onthouden zolang de bestanden niet veranderen.
    /// </summary>
    public static int IngesteldeMaxContext()
    {
        try
        {
            var model = IngesteldModel();
            var gewijzigd = File.Exists(ProviderPad) ? File.GetLastWriteTimeUtc(ProviderPad) : DateTime.MinValue;
            lock (Slot)
            {
                if (_grensCache is { } cache && cache.Gewijzigd == gewijzigd && string.Equals(cache.Model, model, StringComparison.OrdinalIgnoreCase))
                {
                    return cache.Grens;
                }

                var grens = ContextLimitUitLijst(model) is > 0 and var gevonden ? gevonden : StandaardGrens;
                _grensCache = (gewijzigd, model, grens);
                return grens;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return StandaardGrens;
        }
    }

    private static (DateTime Gewijzigd, string Model, int Grens)? _grensCache;

    /// <summary>De context_limit van dit model in jabasoft.json, of 0 als het er niet (of zonder grens) in staat.</summary>
    private static int ContextLimitUitLijst(string model)
    {
        if (model.Length == 0 || !File.Exists(ProviderPad))
        {
            return 0;
        }

        var json = JsonNode.Parse(File.ReadAllText(ProviderPad)) as JsonObject;
        var bestaand = (json?["models"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(m => string.Equals(m["name"]?.GetValue<string>(), model, StringComparison.OrdinalIgnoreCase));

        return bestaand?["context_limit"] is JsonValue waarde && waarde.TryGetValue<int>(out var limiet) ? limiet : 0;
    }

    public async Task<GooseStatus> LeesAsync(CancellationToken ct)
    {
        var lokaleModellen = await hermes.LokaleModellenAsync(ct);

        if (!File.Exists(ConfigPad))
        {
            return GooseStatus.Leeg with { ConfigMap = ConfigMap, LokaleModellen = lokaleModellen };
        }

        YamlMappingNode root;
        lock (Slot)
        {
            root = LaadRoot();

            // Eenmalig omzetten naar het eigen Goose-adres, ook zonder dat er
            // eerst een model gekozen wordt - anders werkt live wisselen pas na
            // de eerste keer kiezen. Geldt na een herstart van Goose.
            try
            {
                ZetGooseAdres();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                log.Add("broker", $"Goose-adres niet bijgewerkt: {ex.Message}");
            }
        }

        var provider = Tekst(root, "active_provider");
        var providerMap = Map(Map(root, "providers"), provider);
        var model = Tekst(providerMap, "model");

        // Op Ollama staat Goose' model in de broker-instelling (zie BewaarAsync).
        var instelling = aiSettings.Current;
        if (instelling.Provider == AiProvider.Ollama)
        {
            var ollamaModel = instelling.Ollama.GooseModel;
            return new GooseStatus(true, Draait(), ConfigMap, provider, ollamaModel, lokaleModellen,
                MaxContext: IngesteldeMaxContext(),
                GeladenContext: OllamaContext.GeladenContext(ollamaModel));
        }

        return new GooseStatus(
            Geinstalleerd: true,
            Draait: Draait(),
            ConfigMap: ConfigMap,
            Provider: provider,
            Model: model,
            LokaleModellen: lokaleModellen,
            MaxContext: IngesteldeMaxContext(),
            GeladenContext: await LmStudioContext.GeladenContextAsync(model, ct));
    }

    public async Task<GooseStatus> BewaarAsync(GooseInstelling nieuw, CancellationToken ct)
    {
        var model = nieuw.Model.Trim();
        if (!File.Exists(ConfigPad))
        {
            return GooseStatus.Leeg with { ConfigMap = ConfigMap, Melding = "Goose is niet geïnstalleerd (geen " + ConfigPad + ")." };
        }

        if (model.Length == 0)
        {
            return (await LeesAsync(ct)) with { Melding = "Geen model gekozen." };
        }

        var lokaleModellen = await hermes.LokaleModellenAsync(ct);
        // De grens: wat de kaart meestuurt, anders wat er nu staat (bij een ander model blijft de keuze dus
        // gelden). Nooit meer dan het model zelf aankan - anders gaat Goose te laat samenvatten.
        var grens = nieuw.MaxContext > 0 ? nieuw.MaxContext : IngesteldeMaxContext();
        var maxContext = lokaleModellen.FirstOrDefault(m => string.Equals(m.Naam, model, StringComparison.OrdinalIgnoreCase))?.MaxContext ?? 0;
        var context = maxContext > 0 ? Math.Min(maxContext, grens) : grens;

        // Op Ollama: het model gaat in de broker-instelling, Goose' config houdt zijn LM Studio-model (nodig als de
        // chatserver terug naar LM Studio gaat; Goose stuurt die naam en de broker zet er het Ollama-model voor in de
        // plaats). De grens wel in jabasoft.json, bij dat LM Studio-model: daar kijkt Goose naar om samen te vatten.
        if (aiSettings.Current.Provider == AiProvider.Ollama)
        {
            var huidig = aiSettings.Current;
            aiSettings.Save(huidig.With(AiProvider.Ollama, huidig.Ollama with { GooseModel = model }));

            try
            {
                var gooseModel = IngesteldModel();
                if (gooseModel.Length > 0)
                {
                    lock (Slot)
                    {
                        ZorgDatModelInLijstStaat(gooseModel, context);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                log.Add("broker", $"Goose-grens niet bewaard: {ex.Message}");
            }

            log.Add("broker", $"Goose ingesteld voor Ollama: '{model}' ({context:N0} tokens context)");
            return await LeesAsync(ct);
        }

        try
        {
            lock (Slot)
            {
                var root = LaadRoot();
                Zet(root, "active_provider", Provider);

                var jabasoft = ZorgVoorMap(ZorgVoorMap(root, "providers"), Provider);
                Zet(jabasoft, "enabled", "true");
                Zet(jabasoft, "model", model);
                Zet(jabasoft, "configured", "true");

                SchrijfRoot(root);
                ZorgDatModelInLijstStaat(model, context);
                ZetGooseAdres();
            }

            log.Add("broker", $"Goose ingesteld: '{model}' ({context:N0} tokens context)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or YamlDotNet.Core.YamlException or JsonException)
        {
            log.Add("broker", $"Goose-instelling niet bewaard: {ex.Message}");
            return (await LeesAsync(ct)) with { Melding = "Niet bewaard: " + ex.Message };
        }

        return await LeesAsync(ct);
    }

    // ------------------------------------------------------------------
    // Bestanden

    /// <summary>Zet het model in de lijst van de Jabasoft-provider (of werkt de context bij als het er al staat).</summary>
    private static void ZorgDatModelInLijstStaat(string model, int context)
    {
        if (!File.Exists(ProviderPad))
        {
            return;
        }

        var json = JsonNode.Parse(File.ReadAllText(ProviderPad)) as JsonObject ?? throw new JsonException("jabasoft.json is geen object.");
        if (json["models"] is not JsonArray modellen)
        {
            modellen = [];
            json["models"] = modellen;
        }

        var bestaand = modellen.OfType<JsonObject>()
            .FirstOrDefault(m => string.Equals(m["name"]?.GetValue<string>(), model, StringComparison.OrdinalIgnoreCase));

        if (bestaand is not null && bestaand["context_limit"]?.GetValue<int>() == context)
        {
            return;
        }

        if (bestaand is null)
        {
            modellen.Add(new JsonObject { ["name"] = model, ["context_limit"] = context });
        }
        else
        {
            bestaand["context_limit"] = context;
        }

        File.Copy(ProviderPad, ProviderPad + ".jabasoft-backup", overwrite: true);
        VervangBestand(ProviderPad, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Wijst de Jabasoft-provider naar <see cref="GooseUrl"/> als hij nog naar het gedeelde adres wijst. Een eigen, ander adres blijft staan.</summary>
    private static void ZetGooseAdres()
    {
        if (!File.Exists(ProviderPad))
        {
            return;
        }

        var json = JsonNode.Parse(File.ReadAllText(ProviderPad)) as JsonObject ?? throw new JsonException("jabasoft.json is geen object.");
        if (!string.Equals(json["base_url"]?.GetValue<string>()?.TrimEnd('/'), OudeUrl, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        json["base_url"] = GooseUrl;
        File.Copy(ProviderPad, ProviderPad + ".jabasoft-backup", overwrite: true);
        VervangBestand(ProviderPad, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static YamlMappingNode LaadRoot()
    {
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
        File.Copy(ConfigPad, ConfigPad + ".jabasoft-backup", overwrite: true);

        using var tekst = new StringWriter();
        new YamlStream(new YamlDocument(root)).Save(tekst, assignAnchors: false);
        VervangBestand(ConfigPad, tekst.ToString());
    }

    /// <summary>Eerst naast het echte bestand, dan in één keer vervangen: Goose leest het mogelijk net op dat moment.</summary>
    private static void VervangBestand(string pad, string inhoud)
    {
        var tijdelijk = pad + ".tmp";
        File.WriteAllText(tijdelijk, inhoud, new System.Text.UTF8Encoding(false));
        File.Move(tijdelijk, pad, overwrite: true);
    }

    private static bool Draait()
    {
        var processen = Process.GetProcessesByName("Goose");
        try
        {
            return processen.Length > 0;
        }
        finally
        {
            foreach (var proces in processen)
            {
                proces.Dispose();
            }
        }
    }

    // ------------------------------------------------------------------
    // Hulp

    private static YamlMappingNode? Map(YamlMappingNode? ouder, string sleutel) =>
        ouder is not null && sleutel.Length > 0 && ouder.Children.TryGetValue(new YamlScalarNode(sleutel), out var kind) ? kind as YamlMappingNode : null;

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
