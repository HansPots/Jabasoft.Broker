using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jabasoft.Broker;

/// <summary>
/// De tegenhanger van <see cref="LmStudioContext"/> voor Ollama, voor Goose en Hermes (zie HermesProxy).
///
/// Ollama kent geen aparte laadopdracht met een context: de context (num_ctx) hoort bij de vraag, en via het
/// OpenAI-adres (/v1) - waar Goose en Hermes mee praten - kun je hem niet meegeven. Dan krijgt het model Ollama's
/// standaard, vaak 4.096 tokens, en Hermes weigert alles onder 64.000.
///
/// Daarom maakt de broker per model en per contextstap een VARIANT aan: "qwen3:30b-jabasoft-64k" is hetzelfde
/// model met num_ctx 65.536. Dat kost geen schijfruimte (Ollama deelt de modelbestanden) en de vraag kan gewoon via
/// /v1 - streaming, tools en afbeeldingen werken dus ongewijzigd. Meegroeien = naar de variant van de volgende stap;
/// Ollama laadt het model dan opnieuw, net als LM Studio bij een grotere context.
///
/// Terug naar een kleinere stap gebeurt met dezelfde regel als in LmStudioContext.Past: pas als de huidige stap
/// meer dan twee keer zo groot is als nodig. Binnen een gesprek groeit de vraag alleen, dus dat gebeurt in de
/// praktijk bij een nieuw gesprek na een lang gesprek.
/// </summary>
internal static class OllamaContext
{
    /// <summary>Herkenning van onze varianten, zodat ze niet in de modellijsten verschijnen.</summary>
    public const string VariantTeken = "-jabasoft-";

    /// <summary>Hoelang een door ons geladen model blijft staan zonder gebruik - gelijk aan LmStudioContext.</summary>
    private const string OntlaadNa = "5m";

    /// <summary>Aanmaken en laden kan even duren (een groot model van schijf lezen).</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private static readonly SemaphoreSlim Slot = new(1, 1);

    /// <summary>Per basismodel de context van de variant die het laatst gebruikt is (en dus waarschijnlijk geladen staat).</summary>
    private static readonly Dictionary<string, int> Laatst = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Varianten waarvan we weten dat ze bestaan - scheelt een vraag aan Ollama bij elke beurt.</summary>
    private static readonly HashSet<string> Bestaat = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per model wat /api/show zei: (grootste context, kan chatten, kan beelden). Verandert niet.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Max, bool Chat, bool Beeld)> Kenmerken =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>De naam van de variant van dit model met deze context.</summary>
    public static string Variant(string model, int context)
    {
        var naam = model.Contains(':', StringComparison.Ordinal) ? model : model + ":latest";
        return $"{naam}{VariantTeken}{context / 1024}k";
    }

    /// <summary>Met hoeveel context de broker dit model het laatst liet draaien (0 = nog niet). Voor de Goose-kaart.</summary>
    public static int GeladenContext(string model)
    {
        lock (Laatst)
        {
            return Laatst.TryGetValue(model, out var context) ? context : 0;
        }
    }

    /// <summary>
    /// De chatmodellen op Ollama met hun grootste context en of ze beelden lezen - zonder embeddingmodellen en
    /// zonder onze eigen varianten. Leeg als Ollama niet bereikbaar is.
    /// </summary>
    public static async Task<List<(string Naam, int Max, bool KanBeelden)>> ModellenAsync(string serverUrl, CancellationToken ct)
    {
        var url = serverUrl.TrimEnd('/');
        var resultaat = new List<(string, int, bool)>();

        try
        {
            using var lijst = await Http.GetAsync(url + "/api/tags", ct);
            if (!lijst.IsSuccessStatusCode)
            {
                return resultaat;
            }

            using var doc = JsonDocument.Parse(await lijst.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("models", out var modellen))
            {
                return resultaat;
            }

            foreach (var item in modellen.EnumerateArray())
            {
                var naam = item.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                if (naam.Length == 0 || naam.Contains(VariantTeken, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var (max, chat, beeld) = await KenmerkenAsync(url, naam, ct);
                if (chat)
                {
                    resultaat.Add((naam, max, beeld));
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            // Ollama niet bereikbaar: dan is er niets te kiezen.
        }

        return resultaat;
    }

    private static async Task<(int Max, bool Chat, bool Beeld)> KenmerkenAsync(string url, string model, CancellationToken ct)
    {
        if (Kenmerken.TryGetValue(model, out var bekend))
        {
            return bekend;
        }

        using var antwoord = await Http.PostAsJsonAsync(url + "/api/show", new { model }, ct);
        if (!antwoord.IsSuccessStatusCode)
        {
            return (0, false, false);
        }

        using var doc = JsonDocument.Parse(await antwoord.Content.ReadAsStringAsync(ct));
        var wortel = doc.RootElement;

        var kan = wortel.TryGetProperty("capabilities", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList()
            : ["completion"];

        var max = 0;
        if (wortel.TryGetProperty("model_info", out var info) && info.ValueKind == JsonValueKind.Object)
        {
            foreach (var veld in info.EnumerateObject())
            {
                if (veld.Name.EndsWith(".context_length", StringComparison.Ordinal) && veld.Value.TryGetInt32(out var waarde))
                {
                    max = waarde;
                }
            }
        }

        var kenmerken = (max, kan.Contains("completion"), kan.Contains("vision"));
        Kenmerken[model] = kenmerken;
        return kenmerken;
    }

    /// <summary>
    /// Zorgt dat er een variant van het model met minstens deze context klaarstaat en geladen is, en geeft de naam
    /// daarvan - die gaat in de vraag in plaats van het model. Fout bevat "videogeheugen" als het model niet paste,
    /// zodat HermesProxy een stap kleiner kan proberen (net als bij LM Studio).
    /// </summary>
    public static async Task<(string Variant, string? Fout)> ZorgVoorAsync(string serverUrl, string model, int context, CancellationToken ct)
    {
        var url = serverUrl.TrimEnd('/');

        await Slot.WaitAsync(ct);
        try
        {
            int huidig;
            lock (Laatst)
            {
                huidig = Laatst.TryGetValue(model, out var c) ? c : 0;
            }

            // Lopen er nog andere vragen op dit model, dan niet herladen: past de huidige stap, prima, anders wachten.
            using var herlaadrecht = RequestGate.ProbeerHerlaadrecht(model);
            if (herlaadrecht is null)
            {
                return huidig >= context ? (Variant(model, huidig), null) : throw new RequestGate.AlleenNodigException();
            }

            // Zelfde regel als LmStudioContext.Past: niet te klein, en niet meer dan twee keer zo groot als nodig.
            if (huidig >= context && huidig <= context * 2)
            {
                return (Variant(model, huidig), null);
            }

            var variant = Variant(model, context);
            var fout = await MaakAanAsync(url, model, variant, context, ct);
            if (fout is not null)
            {
                return (variant, fout);
            }

            // Het grote model van LM Studio uit het geheugen, en onze vorige stap van dit model: in 12 GB
            // videogeheugen past maar één groot model tegelijk.
            await LmStudioContext.OntlaadGroteAsync(ct);
            if (huidig > 0 && huidig != context)
            {
                await OntlaadAsync(url, Variant(model, huidig), ct);
            }

            var laadfout = await LaadAsync(url, variant, ct);
            if (laadfout is not null)
            {
                return (variant, laadfout);
            }

            lock (Laatst)
            {
                Laatst[model] = context;
            }

            _url = url;
            return (variant, null);
        }
        finally
        {
            Slot.Release();
        }
    }

    /// <summary>
    /// Haalt de varianten die de broker in Ollama geladen heeft uit het geheugen - voordat LM Studio een groot model
    /// laadt (zie LmStudioContext). Doet niets als Ollama er niet is.
    /// </summary>
    public static async Task OntlaadOnzeAsync(CancellationToken ct)
    {
        List<(string Model, int Context)> geladen;
        lock (Laatst)
        {
            geladen = [.. Laatst.Select(p => (p.Key, p.Value))];
            Laatst.Clear();
        }

        if (_url is not { } url)
        {
            return;
        }

        foreach (var (model, context) in geladen)
        {
            await OntlaadAsync(url, Variant(model, context), ct);
        }
    }

    /// <summary>Het Ollama-adres van de laatste keer dat de broker er een variant laadde - voor <see cref="OntlaadOnzeAsync"/>.</summary>
    private static string? _url;

    /// <summary>Maakt de variant aan als hij er nog niet is. Null = klaar, anders de fout.</summary>
    private static async Task<string?> MaakAanAsync(string url, string model, string variant, int context, CancellationToken ct)
    {
        if (Bestaat.Contains(variant))
        {
            return null;
        }

        try
        {
            using (var bestaat = await Http.PostAsJsonAsync(url + "/api/show", new { model = variant }, ct))
            {
                if (bestaat.IsSuccessStatusCode)
                {
                    Bestaat.Add(variant);
                    return null;
                }
            }

            var verzoek = new JsonObject
            {
                ["model"] = variant,
                ["from"] = model,
                ["parameters"] = new JsonObject { ["num_ctx"] = context },
                ["stream"] = false,
            };

            using var antwoord = await Http.PostAsync(url + "/api/create",
                new StringContent(verzoek.ToJsonString(), System.Text.Encoding.UTF8, "application/json"), ct);

            if (!antwoord.IsSuccessStatusCode)
            {
                return $"Ollama kon {variant} niet aanmaken: {await antwoord.Content.ReadAsStringAsync(ct)}";
            }

            Bestaat.Add(variant);
            return null;
        }
        catch (HttpRequestException ex)
        {
            return $"Ollama niet bereikbaar op {url}: {ex.Message}";
        }
    }

    /// <summary>Laadt de variant alvast (een vraag zonder prompt), zodat een vol videogeheugen hier al blijkt.</summary>
    private static async Task<string?> LaadAsync(string url, string variant, CancellationToken ct)
    {
        try
        {
            using var antwoord = await Http.PostAsJsonAsync(url + "/api/generate", new { model = variant, keep_alive = OntlaadNa, stream = false }, ct);
            if (antwoord.IsSuccessStatusCode)
            {
                return null;
            }

            var tekst = await antwoord.Content.ReadAsStringAsync(ct);
            var geheugen = tekst.Contains("memory", StringComparison.OrdinalIgnoreCase)
                || tekst.Contains("allocate", StringComparison.OrdinalIgnoreCase)
                || tekst.Contains("cuda", StringComparison.OrdinalIgnoreCase);

            return geheugen
                ? $"{variant} kon niet geladen worden in Ollama: te weinig videogeheugen. {tekst}"
                : $"{variant} kon niet geladen worden in Ollama: {tekst}";
        }
        catch (HttpRequestException ex)
        {
            return $"Ollama niet bereikbaar op {url}: {ex.Message}";
        }
    }

    private static async Task OntlaadAsync(string url, string variant, CancellationToken ct)
    {
        try
        {
            using var _ = await Http.PostAsJsonAsync(url + "/api/generate", new { model = variant, keep_alive = 0, stream = false }, ct);
        }
        catch (HttpRequestException)
        {
            // Ollama weg: dan staat er ook niets geladen.
        }
    }
}
