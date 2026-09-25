using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Jabasoft.Base.AiBroker;

namespace Jabasoft.Broker;

/// <summary>
/// Hetzelfde gesprek als in <see cref="ProviderClient"/>, maar streamend:
/// het antwoord komt stukje bij beetje van de server en wordt meteen
/// doorgegeven, in plaats van pas als het af is.
///
/// Staat apart van ProviderClient omdat de twee providers hier ECHT van
/// elkaar verschillen: LM Studio spreekt het OpenAI-formaat (SSE: regels
/// die met "data: " beginnen en eindigen op "[DONE]"), Ollama stuurt
/// gewoon één JSON-object per regel. Dat in dezelfde klasse proppen als
/// de gewone aanroep maakt allebei moeilijker te volgen.
///
/// De tokens komen pas aan het eind. LM Studio geeft ze alleen als je
/// erom vraagt (stream_options.include_usage), Ollama zet ze in de
/// laatste regel bij done=true.
/// </summary>
internal sealed class ProviderStream(IHttpClientFactory httpClientFactory)
{
    public IAsyncEnumerable<ChatStreamChunk> ChatAsync(
        AiProvider provider,
        string serverUrl,
        string model,
        IReadOnlyList<ChatMessage> messages,
        double? temperature,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return Fout("No server URL configured.");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            return Fout("No chat model configured.");
        }

        var client = httpClientFactory.CreateClient("chat");

        return provider switch
        {
            AiProvider.Ollama => OllamaAsync(client, serverUrl, model, messages, temperature, cancellationToken),
            AiProvider.LmStudio => LmStudioAsync(client, serverUrl, model, messages, temperature, cancellationToken),
            _ => Fout($"Unsupported provider: {provider}."),
        };
    }

    /// <summary>Eén stukje met alleen een reden erin - hoe een mislukking eruitziet voor wie meeluistert.</summary>
    private static async IAsyncEnumerable<ChatStreamChunk> Fout(string reden)
    {
        yield return new ChatStreamChunk(Done: true, ErrorMessage: reden);
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ChatStreamChunk> LmStudioAsync(
        HttpClient client,
        string serverUrl,
        string model,
        IReadOnlyList<ChatMessage> messages,
        double? temperature,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // LM Studio's EIGEN api (/api/v1/chat) is nodig voor het percentage
        // tijdens het inlezen van de prompt (zie ChatStreamChunk.PromptProgress) -
        // de OpenAI-compatibele /v1/chat/completions geeft dat niet, geverifieerd
        // rechtstreeks tegen een draaiende server. Maar deze api kent geen losse
        // rol-berichten zoals messages[]: "input" is één tekst (of tekst/
        // afbeelding-stukken zonder rol), en geschiedenis onthoudt de SERVER zelf
        // via een response_id in plaats van dat je hem meestuurt - ook getest,
        // en niet hoe deze familie werkt (elke aanroep stuurt zelf de volledige
        // context mee, zie Projectassistent/_verloop). Vandaar PlatSlaan
        // hieronder: het systeembericht blijft apart (system_prompt bestaat wel),
        // de rest (bestanden, eerdere beurten, de vraag) wordt plat tot gewone
        // tekst met de rol als kopje erboven - zelfde volledige-context-per-
        // aanroep als altijd, alleen anders verpakt. Geverifieerd dat het model
        // zo'n platgeslagen geschiedenis nog gewoon volgt (zie het testgesprek
        // met "Piet" tijdens het bouwen hiervan).
        var (systeemPrompt, invoer) = PlatSlaan(messages);

        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["input"] = invoer,
            ["stream"] = true,
        };

        if (systeemPrompt is not null)
        {
            payload["system_prompt"] = systeemPrompt;
        }

        if (temperature.HasValue)
        {
            payload["temperature"] = temperature.Value;
        }

        string? huidigEvent = null;

        ChatStreamChunk? LeesRegel(string regel)
        {
            if (regel.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                huidigEvent = regel[6..].Trim();
                return null;
            }

            if (!regel.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var inhoud = regel[5..].Trim();
            var soort = huidigEvent;
            huidigEvent = null;

            return inhoud.Length == 0 || soort is null ? null : LeesLmStudioEvent(soort, inhoud);
        }

        await foreach (var stukje in LeesAsync(
            client,
            ProviderClient.CombineUrl(serverUrl, "/api/v1/chat"),
            payload,
            "LM Studio",
            LeesRegel,
            cancellationToken))
        {
            yield return stukje;
        }
    }

    /// <summary>
    /// Splitst de berichtenlijst in het systeembericht (los, want dat kent
    /// /api/v1/chat wél als system_prompt) en de rest, plat geslagen tot één
    /// tekst met de rol als kopje - zie de toelichting bij LmStudioAsync voor
    /// waarom dit nodig is en wat er niet meer klopt (geen chat-template-
    /// rolmarkering per beurt meer, alleen platte tekst).
    /// </summary>
    private static (string? SysteemPrompt, object Invoer) PlatSlaan(IReadOnlyList<ChatMessage> messages)
    {
        var systeem = messages.Where(m => m.Role == "system").Select(m => m.Content).ToList();
        var rest = messages.Where(m => m.Role != "system").ToList();

        var invoer = new StringBuilder();

        foreach (var bericht in rest)
        {
            if (invoer.Length > 0)
            {
                invoer.Append("\n\n");
            }

            invoer.Append(bericht.Role.ToUpperInvariant()).Append(":\n").Append(bericht.Content);
        }

        var systeemPrompt = systeem.Count > 0 ? string.Join("\n\n", systeem) : null;

        // Met afbeeldingen wordt "input" een lijst stukken: eerst de tekst,
        // dan elke afbeelding als data-url (het formaat van /api/v1/chat).
        var afbeeldingen = rest.SelectMany(m => m.Images ?? []).ToList();

        if (afbeeldingen.Count == 0)
        {
            return (systeemPrompt, invoer.ToString());
        }

        var stukken = new List<object> { new { type = "text", content = invoer.ToString() } };
        stukken.AddRange(afbeeldingen.Select(b64 => (object)new { type = "image", data_url = $"data:image/png;base64,{b64}" }));

        return (systeemPrompt, stukken);
    }

    private static async IAsyncEnumerable<ChatStreamChunk> OllamaAsync(
        HttpClient client,
        string serverUrl,
        string model,
        IReadOnlyList<ChatMessage> messages,
        double? temperature,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages.Select(m => m.Images is { Count: > 0 }
                ? (object)new { role = m.Role, content = m.Content, images = m.Images }
                : new { role = m.Role, content = m.Content }),
            ["stream"] = true,
        };

        if (temperature.HasValue)
        {
            payload["options"] = new { temperature = temperature.Value };
        }

        await foreach (var stukje in LeesAsync(
            client,
            ProviderClient.CombineUrl(serverUrl, "/api/chat"),
            payload,
            "Ollama",
            LeesOllamaRegel,
            cancellationToken))
        {
            yield return stukje;
        }
    }

    /// <summary>
    /// Het gemeenschappelijke deel: versturen, regel voor regel lezen, en
    /// elke regel door de vertaler van de betreffende provider halen.
    /// Eindigt ALTIJD met een stukje waar Done op staat - ook als de
    /// verbinding wegvalt, want anders blijft de aanroeper wachten.
    /// </summary>
    private static async IAsyncEnumerable<ChatStreamChunk> LeesAsync(
        HttpClient client,
        string url,
        Dictionary<string, object> payload,
        string naam,
        Func<string, ChatStreamChunk?> vertaal,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        string? fout = null;

        using var bericht = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload),
        };

        // Eerst opvangen, dan pas teruggeven: C# staat geen yield in een
        // catch toe. Zo is er bovendien één plek waar het einde vandaan
        // komt.
        try
        {
            response = await client.SendAsync(bericht, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            fout = "Timed out waiting for a response.";
        }
        catch (HttpRequestException ex)
        {
            fout = ConnectionErrorFormatter.Describe(ex, url);
        }

        if (fout is not null || response is null)
        {
            yield return new ChatStreamChunk(Done: true, ErrorMessage: fout ?? $"No response from {naam}.");
            yield break;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                yield return new ChatStreamChunk(Done: true, ErrorMessage: $"{naam} returned {(int)response.StatusCode}: {Kort(body)}");
                yield break;
            }

            using var stroom = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var lezer = new StreamReader(stroom);

            long promptTokens = 0;
            long completionTokens = 0;

            while (true)
            {
                string? regel = null;

                try
                {
                    regel = await lezer.ReadLineAsync(cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
                {
                    fout = $"The answer from {naam} stopped halfway: {ex.Message}";
                }

                if (fout is not null)
                {
                    yield return new ChatStreamChunk(Done: true, ErrorMessage: fout);
                    yield break;
                }

                if (regel is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(regel))
                {
                    continue;
                }

                ChatStreamChunk? stukje = null;

                try
                {
                    stukje = vertaal(regel);
                }
                catch (JsonException)
                {
                    // Een regel die we niet kunnen lezen slaan we over; het
                    // einde komt toch van de stroom zelf.
                }

                if (stukje is null)
                {
                    continue;
                }

                if (stukje.ErrorMessage is not null)
                {
                    yield return stukje with { Done = true };
                    yield break;
                }

                // De tokens komen meestal in een LAATSTE stukje zonder
                // tekst; onthouden en pas bij het afsluiten meegeven.
                if (stukje.PromptTokens > 0)
                {
                    promptTokens = stukje.PromptTokens;
                }

                if (stukje.CompletionTokens > 0)
                {
                    completionTokens = stukje.CompletionTokens;
                }

                if (!string.IsNullOrEmpty(stukje.Text))
                {
                    yield return new ChatStreamChunk(stukje.Text);
                }
                else if (stukje.Thinking)
                {
                    yield return new ChatStreamChunk(Thinking: true);
                }
                else if (stukje.PromptProgress.HasValue)
                {
                    yield return new ChatStreamChunk(PromptProgress: stukje.PromptProgress);
                }

                if (stukje.Done)
                {
                    break;
                }
            }

            // Het afsluitende stukje, met wat de aanroep gekost heeft. Dit
            // komt er altijd, ook als de server niets over tokens zei -
            // anders weet de aanroeper niet dat hij klaar is.
            yield return new ChatStreamChunk(
                Done: true,
                PromptTokens: promptTokens,
                CompletionTokens: completionTokens);
        }
    }

    /// <summary>
    /// LM Studio's eigen (niet-OpenAI) formaat: elk "event: soort" hoort bij de
    /// "data: {...}" die erna komt (zie LmStudioAsync, dat de twee regels aan
    /// elkaar plakt). Onbekende/oninteressante soorten (chat.start,
    /// model_load.*, prompt_processing.start/end, message.start/end,
    /// tool_call.*) leveren niets op - alleen de vier hieronder doen iets met
    /// het scherm.
    ///
    /// Geverifieerd tegen een echt draaiende LM Studio-server (niet alleen
    /// tegen de documentatie) - zie het testgesprek dat bij het bouwen hiervan
    /// gevoerd is: message.delta.content, chat.end.result.stats.input_tokens/
    /// total_output_tokens en prompt_processing.progress.progress kloppen zo.
    /// </summary>
    private static ChatStreamChunk? LeesLmStudioEvent(string soort, string inhoud)
    {
        using var document = JsonDocument.Parse(inhoud);
        var wortel = document.RootElement;

        switch (soort)
        {
            case "error":
            {
                var melding = wortel.TryGetProperty("error", out var foutElement)
                    ? (foutElement.TryGetProperty("message", out var meldingElement) ? meldingElement.GetString() : foutElement.ToString())
                    : "unknown error";
                return new ChatStreamChunk(ErrorMessage: $"LM Studio error: {melding}");
            }

            // Het percentage waar dit allemaal om begonnen is - hoe ver het
            // inlezen van de prompt is, vóór het model met antwoorden begint.
            case "prompt_processing.progress":
                return wortel.TryGetProperty("progress", out var voortgangElement) && voortgangElement.ValueKind == JsonValueKind.Number
                    ? new ChatStreamChunk(PromptProgress: voortgangElement.GetDouble())
                    : null;

            case "message.delta":
            {
                var tekst = wortel.TryGetProperty("content", out var inhoudElement) && inhoudElement.ValueKind == JsonValueKind.String
                    ? inhoudElement.GetString()
                    : null;
                return string.IsNullOrEmpty(tekst) ? null : new ChatStreamChunk(tekst);
            }

            // Een redenerend model (Qwen3 en soortgenoten) - net als bij Ollama/de
            // oude OpenAI-vorm geven we alleen door DAT er gedacht wordt, niet de
            // inhoud: die hoort niet op het scherm.
            case "reasoning.delta":
                return new ChatStreamChunk(Thinking: true);

            case "chat.end":
            {
                if (!wortel.TryGetProperty("result", out var resultaat) || !resultaat.TryGetProperty("stats", out var stats))
                {
                    return new ChatStreamChunk(Done: true);
                }

                var prompt = stats.TryGetProperty("input_tokens", out var promptElement) ? promptElement.GetInt64() : 0;
                var completion = stats.TryGetProperty("total_output_tokens", out var completionElement) ? completionElement.GetInt64() : 0;
                return new ChatStreamChunk(Done: true, PromptTokens: prompt, CompletionTokens: completion);
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Ollama stuurt geen SSE maar kale JSON-regels: de tekst in
    /// message.content, en de laatste regel draagt done=true met
    /// prompt_eval_count en eval_count.
    /// </summary>
    private static ChatStreamChunk? LeesOllamaRegel(string regel)
    {
        using var document = JsonDocument.Parse(regel);
        var wortel = document.RootElement;

        if (wortel.TryGetProperty("error", out var foutElement))
        {
            return new ChatStreamChunk(ErrorMessage: $"Ollama error: {foutElement.GetString()}");
        }

        string? tekst = null;
        var denkt = false;

        if (wortel.TryGetProperty("message", out var bericht))
        {
            if (bericht.TryGetProperty("content", out var inhoudElement))
            {
                tekst = inhoudElement.GetString();
            }

            // Een redenerend model (Qwen3 en soortgenoten) zet zijn
            // nadenken in message.thinking, los van de eigenlijke inhoud.
            // Net als bij LM Studio (reasoning.delta) geven we alleen door DAT
            // er gedacht wordt, niet wat: de inhoud hoort niet op het scherm,
            // maar de teller moet wel meelopen - anders staat die stil zolang
            // het model nadenkt.
            denkt = bericht.TryGetProperty("thinking", out var denkElement) &&
                    denkElement.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrEmpty(denkElement.GetString());
        }

        var klaar = wortel.TryGetProperty("done", out var doneElement) &&
                    doneElement.ValueKind == JsonValueKind.True;

        long prompt = wortel.TryGetProperty("prompt_eval_count", out var promptElement) ? promptElement.GetInt64() : 0;
        long completion = wortel.TryGetProperty("eval_count", out var evalElement) ? evalElement.GetInt64() : 0;

        return new ChatStreamChunk(tekst, klaar, prompt, completion, Thinking: denkt);
    }

    private static string Kort(string tekst) => tekst.Length <= 300 ? tekst : tekst[..300] + "…";
}
