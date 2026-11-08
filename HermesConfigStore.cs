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

    private static readonly Lock Slot = new();

    private static string HermesMap =>
        Environment.GetEnvironmentVariable("HERMES_HOME") is { Length: > 0 } eigen
            ? eigen
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes");

    private static string ConfigPad => Path.Combine(HermesMap, "config.yaml");

    /// <summary>Het LM Studio-adres zoals Hermes het wil: met /v1 erachter (OpenAI-compatibel).</summary>
    private string LokaalServerUrl => aiSettings.Current.LmStudio.Url.TrimEnd('/') + "/v1";

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

        return new HermesStatus(
            Geinstalleerd: true,
            GatewayDraait: GatewayDraait(),
            HermesMap: HermesMap,
            Provider: provider,
            Model: Tekst(model, "default") is { Length: > 0 } standaard ? standaard : Tekst(model, "model"),
            Sleutel: SleutelStatus(provider),
            LokaalModel: Tekst(compressie, "model"),
            LokaalServerUrl: LokaalServerUrl,
            JabasoftMcp: Map(root, "mcp_servers")?.Children.ContainsKey(new YamlScalarNode("jabasoft")) == true,
            LokaleModellen: lokaleModellen);
    }

    public async Task<HermesStatus> BewaarAsync(HermesInstelling nieuw, CancellationToken ct)
    {
        if (!Directory.Exists(HermesMap))
        {
            return HermesStatus.Leeg with { HermesMap = HermesMap, Melding = "Hermes Agent is niet geïnstalleerd (geen map " + HermesMap + ")." };
        }

        try
        {
            lock (Slot)
            {
                var root = LaadRoot();

                // --- Hoofdmodel -------------------------------------------
                var model = ZorgVoorMap(root, "model");
                Zet(model, "provider", nieuw.Provider.Trim());
                Zet(model, "default", nieuw.Model.Trim());

                // Een lokale provider heeft een adres nodig; een cloudprovider
                // juist niet (dan zou Hermes naar LM Studio sturen).
                if (IsLokaal(nieuw.Provider))
                {
                    Zet(model, "base_url", LokaalServerUrl);
                }
                else
                {
                    model.Children.Remove(new YamlScalarNode("base_url"));
                }

                // --- Lokaal model: bijtaken en reserve --------------------
                var auxiliary = ZorgVoorMap(root, "auxiliary");
                var lokaal = nieuw.LokaalModel.Trim();

                if (lokaal.Length == 0)
                {
                    auxiliary.Children.Remove(new YamlScalarNode("compression"));
                    auxiliary.Children.Remove(new YamlScalarNode("vision"));
                    root.Children.Remove(new YamlScalarNode("fallback_providers"));
                }
                else
                {
                    var compressie = ZorgVoorMap(auxiliary, "compression");
                    Zet(compressie, "model", lokaal);
                    Zet(compressie, "base_url", LokaalServerUrl);

                    // Vision alleen als het model ook beelden kan lezen;
                    // anders weg, dan doet Hermes' hoofdmodel het (auto).
                    if (KanBeelden(lokaal))
                    {
                        var vision = ZorgVoorMap(auxiliary, "vision");
                        Zet(vision, "model", lokaal);
                        Zet(vision, "base_url", LokaalServerUrl);
                    }
                    else
                    {
                        auxiliary.Children.Remove(new YamlScalarNode("vision"));
                    }

                    root.Children[new YamlScalarNode("fallback_providers")] = new YamlSequenceNode(
                        new YamlMappingNode(
                            new YamlScalarNode("provider"), new YamlScalarNode("lmstudio"),
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
                    mcp.Children[new YamlScalarNode("jabasoft")] = new YamlMappingNode(
                        new YamlScalarNode("url"), new YamlScalarNode(JabasoftMcpUrl),
                        new YamlScalarNode("timeout"), new YamlScalarNode("120"),
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
            return !proces.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException or IOException or FormatException)
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
    /// chatten.
    /// </summary>
    private async Task<IReadOnlyList<HermesLokaalModel>> LokaleModellenAsync(CancellationToken ct)
    {
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
                    m.TryGetProperty("max_context_length", out var max) && max.TryGetInt32(out var waarde) ? waarde : 0))
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
