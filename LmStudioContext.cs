using System.Diagnostics;
using System.Text.Json;

namespace Jabasoft.Broker;

/// <summary>
/// LM Studio legt de context vast op het moment dat een model geladen wordt (anders dan Ollama, waar je hem per
/// aanvraag meegeeft). Een model dat LM Studio zelf laadt krijgt zijn eigen standaard, bij Qwen3-14B 32.768 tokens,
/// en dat is ongeveer 5 GB videogeheugen voor een venster dat meestal bijna leeg blijft.
///
/// Daarom laden we het model zelf, met de context die deze aanvraag nodig heeft (zie OllamaDefaults.BeginContext),
/// via LM Studio's opdrachtregelprogramma `lms`. Dat doet alleen iets als `lms` er is; zonder laat de broker
/// alles aan LM Studio over, zoals voorheen.
///
/// Wat we NIET aanraken: een model dat iemand zelf in LM Studio geladen heeft (herkenbaar aan het ontbreken van een
/// automatische ontlaadtijd). Zo'n model blijft zoals het is, tenzij zijn context te klein is voor de aanvraag.
/// </summary>
internal static class LmStudioContext
{
    /// <summary>Hoelang een door ons geladen model blijft staan zonder gebruik. Gelijk aan wat Ollama doet (5 minuten).</summary>
    private const int OntlaadNaSeconden = 300;

    private static readonly TimeSpan LmsTijdlimiet = TimeSpan.FromMinutes(3);

    /// <summary>Eén tegelijk: twee aanvragen die tegelijk hetzelfde model herladen komen elkaar in de weg.</summary>
    private static readonly SemaphoreSlim Slot = new(1, 1);

    private static readonly string? Lms = ZoekLms();

    private sealed record Geladen(string Identifier, int Context, bool Handmatig);

    /// <summary>
    /// Zorgt dat het model geladen staat met minstens deze context. Geeft true als dat is geregeld (of al zo was),
    /// false als we er niets aan kunnen doen; de aanroeper gaat dan gewoon door zonder opnieuw te proberen.
    /// </summary>
    public static async Task<bool> ZorgVoorAsync(string model, int context, CancellationToken cancellationToken)
    {
        if (Lms is null)
        {
            return false;
        }

        await Slot.WaitAsync(cancellationToken);
        try
        {
            var geladen = await ZoekGeladenAsync(model, cancellationToken);

            if (geladen is not null && Past(geladen, context))
            {
                return true;
            }

            if (geladen is not null)
            {
                await VoerUitAsync($"unload \"{geladen.Identifier}\"", cancellationToken);
            }

            return await VoerUitAsync($"load \"{model}\" -c {context} --ttl {OntlaadNaSeconden} -y", cancellationToken);
        }
        catch (Exception)
        {
            // lms ontbreekt, hangt of geeft een fout: dan doet LM Studio het zelf, zoals voor deze functie bestond.
            return false;
        }
        finally
        {
            Slot.Release();
        }
    }

    /// <summary>
    /// Is het geladen model goed genoeg? Te klein: nooit. Te groot: alleen een model dat wij (of LM Studio zelf)
    /// geladen hebben wordt kleiner herladen; een handmatig geladen model laten we staan.
    /// </summary>
    private static bool Past(Geladen geladen, int nodig)
    {
        if (geladen.Context < nodig)
        {
            return false;
        }

        return geladen.Handmatig || geladen.Context <= Math.Max(nodig * 2, 4096);
    }

    private static async Task<Geladen?> ZoekGeladenAsync(string model, CancellationToken cancellationToken)
    {
        var (gelukt, uitvoer) = await LeesAsync("ps --json", cancellationToken);
        if (!gelukt || string.IsNullOrWhiteSpace(uitvoer))
        {
            return null;
        }

        using var document = JsonDocument.Parse(uitvoer);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var sleutel = item.TryGetProperty("modelKey", out var k) ? k.GetString() : null;
            var identifier = item.TryGetProperty("identifier", out var i) ? i.GetString() : null;

            if (!string.Equals(sleutel, model, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(identifier, model, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var context = item.TryGetProperty("contextLength", out var c) && c.TryGetInt32(out var waarde) ? waarde : 0;
            var heeftTtl = item.TryGetProperty("ttlMs", out var t) && t.ValueKind == JsonValueKind.Number;
            return new Geladen(identifier ?? sleutel ?? model, context, Handmatig: !heeftTtl);
        }

        return null;
    }

    private static async Task<bool> VoerUitAsync(string argumenten, CancellationToken cancellationToken)
    {
        var (gelukt, _) = await LeesAsync(argumenten, cancellationToken);
        return gelukt;
    }

    /// <summary>Voert `lms` uit en geeft wat het op de standaarduitvoer zei. De voortgangsbalk van `load` negeren we.</summary>
    private static async Task<(bool Gelukt, string Uitvoer)> LeesAsync(string argumenten, CancellationToken cancellationToken)
    {
        using var proces = Process.Start(new ProcessStartInfo
        {
            FileName = Lms!,
            Arguments = argumenten,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        if (proces is null)
        {
            return (false, string.Empty);
        }

        using var limiet = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limiet.CancelAfter(LmsTijdlimiet);

        var uitvoer = proces.StandardOutput.ReadToEndAsync(limiet.Token);
        _ = proces.StandardError.ReadToEndAsync(limiet.Token);

        try
        {
            await proces.WaitForExitAsync(limiet.Token);
            return (proces.ExitCode == 0, await uitvoer);
        }
        catch (OperationCanceledException)
        {
            try
            {
                proces.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Al klaar.
            }

            throw;
        }
    }

    private static string? ZoekLms()
    {
        var pad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio", "bin", "lms.exe");
        return File.Exists(pad) ? pad : null;
    }
}
