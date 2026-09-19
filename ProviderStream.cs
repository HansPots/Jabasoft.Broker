using System.Net.Http.Json;
using System.Runtime.CompilerServices;
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
        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }),
            ["stream"] = true,
            // Zonder dit stuurt LM Studio bij een stream helemaal geen
            // tokenaantallen, en dan valt er achteraf niets te boeken.
            ["stream_options"] = new { include_usage = true },
        };

        if (temperature.HasValue)
        {
            payload["temperature"] = temperature.Value;
        }

        await foreach (var stukje in LeesAsync(
            client,
            ProviderClient.CombineUrl(serverUrl, "/v1/chat/completions"),
            payload,
            "LM Studio",
            LeesLmStudioRegel,
            cancellationToken))
        {
            yield return stukje;
        }
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
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }),
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
    /// Het OpenAI-formaat: regels beginnen met "data: ", en "[DONE]" sluit
    /// af. De tekst zit in choices[0].delta.content; het allerlaatste
    /// stukje heeft geen choices maar wel usage.
    /// </summary>
    private static ChatStreamChunk? LeesLmStudioRegel(string regel)
    {
        if (!regel.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var inhoud = regel[5..].Trim();

        if (inhoud.Length == 0)
        {
            return null;
        }

        if (inhoud == "[DONE]")
        {
            return new ChatStreamChunk(Done: true);
        }

        using var document = JsonDocument.Parse(inhoud);
        var wortel = document.RootElement;

        if (wortel.TryGetProperty("error", out var foutElement))
        {
            var melding = foutElement.TryGetProperty("message", out var meldingElement)
                ? meldingElement.GetString()
                : foutElement.ToString();
            return new ChatStreamChunk(ErrorMessage: $"LM Studio error: {melding}");
        }

        string? tekst = null;
        var denkt = false;

        if (wortel.TryGetProperty("choices", out var keuzes) && keuzes.GetArrayLength() > 0 &&
            keuzes[0].TryGetProperty("delta", out var delta))
        {
            if (delta.TryGetProperty("content", out var inhoudElement) &&
                inhoudElement.ValueKind == JsonValueKind.String)
            {
                tekst = inhoudElement.GetString();
            }

            // Een redenerend model (Qwen3 en soortgenoten) stuurt zijn
            // overweging in een APART veld, niet in content. Die tekst
            // hoort niet op het scherm, maar het is wel het enige teken
            // dat er gewerkt wordt - soms tientallen seconden lang. Dus
            // geven we door DAT er gedacht wordt, zonder wat.
            if (delta.TryGetProperty("reasoning_content", out var denkElement) &&
                denkElement.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(denkElement.GetString()))
            {
                denkt = true;
            }
        }

        long prompt = 0;
        long completion = 0;

        if (wortel.TryGetProperty("usage", out var verbruik) && verbruik.ValueKind == JsonValueKind.Object)
        {
            if (verbruik.TryGetProperty("prompt_tokens", out var promptElement))
            {
                prompt = promptElement.GetInt64();
            }

            if (verbruik.TryGetProperty("completion_tokens", out var completionElement))
            {
                completion = completionElement.GetInt64();
            }
        }

        return new ChatStreamChunk(tekst, PromptTokens: prompt, CompletionTokens: completion, Thinking: denkt);
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

        if (wortel.TryGetProperty("message", out var bericht) &&
            bericht.TryGetProperty("content", out var inhoudElement))
        {
            tekst = inhoudElement.GetString();
        }

        var klaar = wortel.TryGetProperty("done", out var doneElement) &&
                    doneElement.ValueKind == JsonValueKind.True;

        long prompt = wortel.TryGetProperty("prompt_eval_count", out var promptElement) ? promptElement.GetInt64() : 0;
        long completion = wortel.TryGetProperty("eval_count", out var evalElement) ? evalElement.GetInt64() : 0;

        return new ChatStreamChunk(tekst, klaar, prompt, completion);
    }

    private static string Kort(string tekst) => tekst.Length <= 300 ? tekst : tekst[..300] + "…";
}
