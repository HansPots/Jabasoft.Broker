using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Jabasoft.Base.AiBroker;
using Jabasoft.Base.Logging;
using ModelContextProtocol.Server;

namespace Jabasoft.Broker;

/// <summary>
/// MCP-tool "teken": maakt een afbeelding met de lokale beeldservice van
/// ImageFunn (ImageFunn.Image/service.py op 127.0.0.1:5330), zodat Hermes en
/// Claude Code kunnen tekenen zonder clouddienst.
///
/// De videokaart (12 GB) kan niet tegelijk een taalmodel in LM Studio en een
/// beeldmodel dragen. Daarom:
/// 1. neemt de tool de beurt van LM Studio in de wachtrij (RequestGate), zodat
///    er tijdens het tekenen geen taalmodelvraag tussendoor komt;
/// 2. haalt hij alle modellen uit LM Studio;
/// 3. start hij zo nodig de beeldservice (zoals ImageFunn dat zelf ook doet);
/// 4. tekent hij, en laat hij de beeldservice daarna meteen weer los
///    (/ontladen), zodat het volgende taalmodel er weer in past.
/// </summary>
[McpServerToolType]
internal sealed class BeeldTools(
    IHttpClientFactory httpClients,
    IConfiguration configuration,
    IWebHostEnvironment omgeving,
    AiSettingsStore settings,
    RequestGate gate,
    AiWachtrij wachtrij,
    ActivityLog log)
{
    // Niet "localhost": uvicorn luistert alleen op 127.0.0.1 en dan kost ::1 eerst 2 seconden (zie ImageServiceClient).
    private const string BeeldServiceUrl = "http://127.0.0.1:5330";
    private static readonly TimeSpan MaxTekenTijd = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxOpstartTijd = TimeSpan.FromMinutes(3);

    /// <summary>De map met service.py en .venv - standaard naast deze repo; te wijzigen met ImageFunn:ServiceMap.</summary>
    private string ServiceMap => configuration["ImageFunn:ServiceMap"] is { Length: > 0 } eigen
        ? eigen
        : Path.GetFullPath(Path.Combine(omgeving.ContentRootPath, "..", "JabaSoft.ImageFunn", "ImageFunn.Image"));

    [McpServerTool(Name = "teken")]
    [Description("Tekent een afbeelding op deze computer met de beeldservice van ImageFunn en geeft het pad van het PNG-bestand terug. Schrijf de omschrijving bij voorkeur in het Engels en concreet (onderwerp, stijl, licht), bijvoorbeeld 'a red sports car on a coastal road at sunset, photorealistic'. Duurt meestal 30 seconden tot een paar minuten.")]
    public async Task<string> Teken(
        [Description("Wat er op de afbeelding moet staan, liefst in het Engels.")] string omschrijving,
        [Description("Breedte in pixels (256-2048, standaard 1024).")] int breedte = 1024,
        [Description("Hoogte in pixels (256-2048, standaard 1024).")] int hoogte = 1024,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(omschrijving))
        {
            return "Geen omschrijving: zeg wat er getekend moet worden.";
        }

        var vraag = wachtrij.Aanmelden("MCP", "Tekenen", "ImageFunn", "beeld", ct);
        string? fout = "Afgebroken.";

        try
        {
            var resultaat = await gate.RunGatedAsync(
                AiProvider.LmStudio,
                settings.Current.LmStudio.Url,
                null, // tekenen haalt alle modellen eruit: de server voor zich alleen
                () => TekenAsync(omschrijving.Trim(), Math.Clamp(breedte, 256, 2048), Math.Clamp(hoogte, 256, 2048), vraag.Token),
                vraag.Token,
                () => wachtrij.Begonnen(vraag));

            fout = resultaat.Gelukt ? null : resultaat.Tekst;
            log.Add("broker", resultaat.Gelukt ? $"Getekend: {omschrijving}" : $"Tekenen mislukt: {resultaat.Tekst}");
            return resultaat.Tekst;
        }
        catch (OperationCanceledException) when (AiWachtrij.IsGeannuleerd(vraag))
        {
            fout = "Geannuleerd.";
            return "Het tekenen is geannuleerd.";
        }
        finally
        {
            wachtrij.Afronden(vraag, fout);
        }
    }

    private async Task<(bool Gelukt, string Tekst)> TekenAsync(string omschrijving, int breedte, int hoogte, CancellationToken ct)
    {
        // 1. Videokaart vrij: het taalmodel eruit.
        await LmStudioContext.OntlaadAllesAsync(ct);

        // 2. De beeldservice moet draaien.
        var http = httpClients.CreateClient("beeld");
        if (!await IsGezondAsync(http, ct) && await StartBeeldServiceAsync(http, ct) is { } startFout)
        {
            return (false, startFout);
        }

        try
        {
            // 3. Opdracht geven en wachten.
            using var start = await http.PostAsJsonAsync(
                BeeldServiceUrl + "/jobs",
                new { prompt = omschrijving, soort = "beeld", breedte, hoogte },
                ct);

            if (!start.IsSuccessStatusCode)
            {
                return (false, "De beeldservice weigerde de opdracht: " + (await start.Content.ReadAsStringAsync(ct)).Trim());
            }

            using var startDoc = JsonDocument.Parse(await start.Content.ReadAsStringAsync(ct));
            var jobId = startDoc.RootElement.GetProperty("job_id").GetString();

            var klok = Stopwatch.StartNew();
            while (klok.Elapsed < MaxTekenTijd)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);

                using var status = JsonDocument.Parse(await http.GetStringAsync($"{BeeldServiceUrl}/jobs/{jobId}", ct));
                var staat = status.RootElement.GetProperty("status").GetString();

                if (staat == "klaar")
                {
                    var bestand = status.RootElement.TryGetProperty("bestand", out var b) ? b.GetString() : null;
                    return bestand is null
                        ? (false, "De beeldservice was klaar maar gaf geen bestand terug.")
                        : (true, $"Klaar. De afbeelding staat in: {bestand}\n\n![{omschrijving}]({new Uri(bestand).AbsoluteUri})");
                }

                if (staat == "mislukt")
                {
                    var reden = status.RootElement.TryGetProperty("foutmelding", out var f) ? f.GetString() : null;
                    return (false, "Het tekenen is mislukt" + (string.IsNullOrWhiteSpace(reden) ? "." : ": " + reden));
                }
            }

            return (false, $"Het tekenen duurde langer dan {MaxTekenTijd.TotalMinutes:0} minuten en is opgegeven.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return (false, "Kon niet met de beeldservice praten: " + ex.Message);
        }
        finally
        {
            // 4. Videokaart weer vrij voor het taalmodel (de service stopt zichzelf daarbij).
            try
            {
                using var _ = await http.PostAsync(BeeldServiceUrl + "/ontladen", null, CancellationToken.None);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Draait al niet meer: dan is het geheugen al vrij.
            }
        }
    }

    private static async Task<bool> IsGezondAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            using var antwoord = await http.GetAsync(BeeldServiceUrl + "/health", ct);
            return antwoord.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>Start service.py op dezelfde manier als ImageFunn (ImageServiceLauncher): uvicorn in de eigen venv, uitvoer naar service.log.</summary>
    private async Task<string?> StartBeeldServiceAsync(HttpClient http, CancellationToken ct)
    {
        var map = ServiceMap;
        var python = Path.Combine(map, ".venv", "Scripts", "python.exe");
        if (!File.Exists(python))
        {
            return $"De beeldservice van ImageFunn is niet gevonden ({python}).";
        }

        var logPad = Path.Combine(map, "service.log");
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/d /s /c \"\"{python}\" -m uvicorn service:app --host 127.0.0.1 --port 5330 >> \"{logPad}\" 2>&1\"",
            WorkingDirectory = map,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        Process.Start(startInfo)?.Dispose();

        var klok = Stopwatch.StartNew();
        while (klok.Elapsed < MaxOpstartTijd)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            if (await IsGezondAsync(http, ct))
            {
                return null;
            }
        }

        return $"De beeldservice startte niet binnen {MaxOpstartTijd.TotalMinutes:0} minuten. Kijk in {logPad}.";
    }
}
