using System.Net.Http.Json;
using System.Text.Json;
using Jabasoft.Base.AiBroker;

namespace Jabasoft.Broker;

/// <summary>
/// The actual outbound HTTP calls to Ollama/LM Studio - one place for both
/// providers' REST shapes, so every JabaSoft app can talk to either
/// through the same <see cref="IAiBrokerClient"/> contract without knowing
/// the difference. Every app reaches this only indirectly, over HTTP via
/// <see cref="IAiBrokerClient"/>.
/// </summary>
internal sealed class ProviderClient(IHttpClientFactory httpClientFactory)
{
    public async Task<ChatResult> ChatAsync(AiProvider provider, string serverUrl, string model, IReadOnlyList<ChatMessage> messages, double? temperature, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return new ChatResult(false, string.Empty, "No server URL configured.");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            return new ChatResult(false, string.Empty, "No chat model configured.");
        }

        var client = httpClientFactory.CreateClient("chat");

        try
        {
            return provider switch
            {
                AiProvider.Ollama => await ChatOllamaAsync(client, serverUrl, model, messages, temperature, cancellationToken),
                AiProvider.LmStudio => await ChatLmStudioAsync(client, serverUrl, model, messages, temperature, cancellationToken),
                _ => new ChatResult(false, string.Empty, $"Unsupported provider: {provider}."),
            };
        }
        catch (OperationCanceledException)
        {
            return new ChatResult(false, string.Empty, "Timed out waiting for a response.");
        }
        catch (HttpRequestException ex)
        {
            return new ChatResult(false, string.Empty, ConnectionErrorFormatter.Describe(ex, serverUrl));
        }
        catch (JsonException ex)
        {
            return new ChatResult(false, string.Empty, $"Got a response, but couldn't parse it: {ex.Message}");
        }
    }

    private static async Task<ChatResult> ChatOllamaAsync(HttpClient client, string serverUrl, string model, IReadOnlyList<ChatMessage> messages, double? temperature, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }),
            ["stream"] = false,
        };

        if (temperature.HasValue)
        {
            payload["options"] = new { temperature = temperature.Value };
        }

        using var response = await client.PostAsJsonAsync(CombineUrl(serverUrl, "/api/chat"), payload, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return new ChatResult(false, string.Empty, $"Ollama returned {(int)response.StatusCode}: {Truncate(body)}");
        }

        using var document = JsonDocument.Parse(body);

        if (document.RootElement.TryGetProperty("error", out var errorElement))
        {
            return new ChatResult(false, string.Empty, $"Ollama error: {errorElement.GetString()}");
        }

        var text = document.RootElement.TryGetProperty("message", out var messageElement) &&
                   messageElement.TryGetProperty("content", out var contentElement)
            ? contentElement.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return new ChatResult(false, string.Empty, "Ollama responded, but with an empty answer.");
        }

        var promptTokens = document.RootElement.TryGetProperty("prompt_eval_count", out var promptElement) ? promptElement.GetInt64() : 0;
        var completionTokens = document.RootElement.TryGetProperty("eval_count", out var evalElement) ? evalElement.GetInt64() : 0;

        return new ChatResult(true, text.Trim(), null, promptTokens, completionTokens);
    }

    private static async Task<ChatResult> ChatLmStudioAsync(HttpClient client, string serverUrl, string model, IReadOnlyList<ChatMessage> messages, double? temperature, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }),
            ["stream"] = false,
        };

        if (temperature.HasValue)
        {
            payload["temperature"] = temperature.Value;
        }

        using var response = await client.PostAsJsonAsync(CombineUrl(serverUrl, "/v1/chat/completions"), payload, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return new ChatResult(false, string.Empty, $"LM Studio returned {(int)response.StatusCode}: {Truncate(body)}");
        }

        using var document = JsonDocument.Parse(body);

        if (document.RootElement.TryGetProperty("error", out var errorElement))
        {
            var errorMessage = errorElement.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : errorElement.ToString();
            return new ChatResult(false, string.Empty, $"LM Studio error: {errorMessage}");
        }

        var text = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();

        if (string.IsNullOrWhiteSpace(text))
        {
            return new ChatResult(false, string.Empty, "LM Studio responded, but with an empty answer.");
        }

        // LM Studio geeft vraag en antwoord apart op (prompt_tokens /
        // completion_tokens), net als Ollama. Geeft een versie alleen een
        // totaal, dan gaat dat volledig naar de promptkant - zie het
        // commentaar bij ChatResult - want een totaal is geen antwoord.
        long promptTokens = 0;
        long completionTokens = 0;

        if (document.RootElement.TryGetProperty("usage", out var usageElement))
        {
            if (usageElement.TryGetProperty("prompt_tokens", out var promptElement))
            {
                promptTokens = promptElement.GetInt64();
            }

            if (usageElement.TryGetProperty("completion_tokens", out var completionElement))
            {
                completionTokens = completionElement.GetInt64();
            }

            if (promptTokens == 0 && completionTokens == 0 &&
                usageElement.TryGetProperty("total_tokens", out var totalTokensElement))
            {
                promptTokens = totalTokensElement.GetInt64();
            }
        }

        return new ChatResult(true, text.Trim(), null, promptTokens, completionTokens);
    }

    public async Task<EmbedResult> EmbedAsync(AiProvider provider, string serverUrl, string model, string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return new EmbedResult(false, [], "No server URL configured.");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            return new EmbedResult(false, [], "No embedding model configured.");
        }

        var client = httpClientFactory.CreateClient("embed");

        try
        {
            return provider switch
            {
                AiProvider.Ollama => await EmbedOllamaAsync(client, serverUrl, model, text, cancellationToken),
                AiProvider.LmStudio => await EmbedLmStudioAsync(client, serverUrl, model, text, cancellationToken),
                _ => new EmbedResult(false, [], $"Unsupported provider: {provider}."),
            };
        }
        catch (OperationCanceledException)
        {
            return new EmbedResult(false, [], "Timed out waiting for a response.");
        }
        catch (HttpRequestException ex)
        {
            return new EmbedResult(false, [], ConnectionErrorFormatter.Describe(ex, serverUrl));
        }
        catch (JsonException ex)
        {
            return new EmbedResult(false, [], $"Got a response, but couldn't parse it: {ex.Message}");
        }
    }

    private static async Task<EmbedResult> EmbedOllamaAsync(HttpClient client, string serverUrl, string model, string text, CancellationToken cancellationToken)
    {
        var payload = new { model, input = text };
        using var response = await client.PostAsJsonAsync(CombineUrl(serverUrl, "/api/embed"), payload, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return new EmbedResult(false, [], $"Ollama returned {(int)response.StatusCode}: {Truncate(body)}");
        }

        using var document = JsonDocument.Parse(body);

        if (document.RootElement.TryGetProperty("error", out var errorElement))
        {
            return new EmbedResult(false, [], $"Ollama error: {errorElement.GetString()}");
        }

        if (document.RootElement.TryGetProperty("embeddings", out var embeddingsElement) && embeddingsElement.GetArrayLength() > 0)
        {
            var vector = embeddingsElement[0].EnumerateArray().Select(e => e.GetSingle()).ToArray();
            var tokensUsed = document.RootElement.TryGetProperty("prompt_eval_count", out var promptElement) ? promptElement.GetInt64() : 0;
            return new EmbedResult(true, vector, null, tokensUsed);
        }

        return new EmbedResult(false, [], "Ollama responded, but with no embedding.");
    }

    private static async Task<EmbedResult> EmbedLmStudioAsync(HttpClient client, string serverUrl, string model, string text, CancellationToken cancellationToken)
    {
        var payload = new { model, input = text };
        using var response = await client.PostAsJsonAsync(CombineUrl(serverUrl, "/v1/embeddings"), payload, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return new EmbedResult(false, [], $"LM Studio returned {(int)response.StatusCode}: {Truncate(body)}");
        }

        using var document = JsonDocument.Parse(body);

        if (document.RootElement.TryGetProperty("error", out var errorElement))
        {
            var errorMessage = errorElement.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : errorElement.ToString();
            return new EmbedResult(false, [], $"LM Studio error: {errorMessage}");
        }

        if (document.RootElement.TryGetProperty("data", out var dataElement) &&
            dataElement.GetArrayLength() > 0 &&
            dataElement[0].TryGetProperty("embedding", out var embeddingElement))
        {
            var vector = embeddingElement.EnumerateArray().Select(e => e.GetSingle()).ToArray();
            var tokensUsed = document.RootElement.TryGetProperty("usage", out var usageElement) &&
                              usageElement.TryGetProperty("total_tokens", out var totalTokensElement)
                ? totalTokensElement.GetInt64()
                : 0;
            return new EmbedResult(true, vector, null, tokensUsed);
        }

        return new EmbedResult(false, [], "LM Studio responded, but with no embedding.");
    }

    public async Task<ModelListResult> ListModelsAsync(AiProvider provider, string serverUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return new ModelListResult(false, [], "No server URL configured.");
        }

        var client = httpClientFactory.CreateClient("catalog");

        try
        {
            return provider switch
            {
                AiProvider.Ollama => await ListOllamaModelsAsync(client, serverUrl, cancellationToken),
                AiProvider.LmStudio => await ListLmStudioModelsAsync(client, serverUrl, cancellationToken),
                _ => new ModelListResult(false, [], $"Unsupported provider: {provider}."),
            };
        }
        catch (OperationCanceledException)
        {
            return new ModelListResult(false, [], "Timed out waiting for a response.");
        }
        catch (HttpRequestException ex)
        {
            return new ModelListResult(false, [], ConnectionErrorFormatter.Describe(ex, serverUrl));
        }
        catch (JsonException ex)
        {
            return new ModelListResult(false, [], $"Got a response, but couldn't parse it: {ex.Message}");
        }
    }

    private static async Task<ModelListResult> ListOllamaModelsAsync(HttpClient client, string serverUrl, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(CombineUrl(serverUrl, "/api/tags"), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return new ModelListResult(false, [], $"Ollama returned {(int)response.StatusCode}: {Truncate(body)}");
        }

        var models = new List<string>();
        using var document = JsonDocument.Parse(body);

        if (document.RootElement.TryGetProperty("models", out var modelsElement))
        {
            foreach (var model in modelsElement.EnumerateArray())
            {
                var name = model.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    models.Add(name);
                }
            }
        }

        return models.Count == 0
            ? new ModelListResult(false, [], "Ollama has no models installed yet (try \"ollama pull <model>\" first).")
            : new ModelListResult(true, models, null);
    }

    private static async Task<ModelListResult> ListLmStudioModelsAsync(HttpClient client, string serverUrl, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(CombineUrl(serverUrl, "/v1/models"), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return new ModelListResult(false, [], $"LM Studio returned {(int)response.StatusCode}: {Truncate(body)}");
        }

        var models = new List<string>();
        using var document = JsonDocument.Parse(body);

        if (document.RootElement.TryGetProperty("data", out var dataElement))
        {
            foreach (var model in dataElement.EnumerateArray())
            {
                var id = model.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    models.Add(id);
                }
            }
        }

        return models.Count == 0
            ? new ModelListResult(false, [], "LM Studio has no models loaded yet.")
            : new ModelListResult(true, models, null);
    }

    public async Task<ConnectionTestResult> TestAsync(AiProvider provider, string serverUrl, string model, CancellationToken cancellationToken)
    {
        const string testPrompt = "Reply with one short, friendly sentence so I know you're working.";

        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return new ConnectionTestResult(false, "No server URL configured.");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            return new ConnectionTestResult(false, "No model configured.");
        }

        var client = httpClientFactory.CreateClient("test");

        try
        {
            return provider switch
            {
                AiProvider.Ollama => await TestOllamaAsync(client, serverUrl, model, testPrompt, cancellationToken),
                AiProvider.LmStudio => await TestLmStudioAsync(client, serverUrl, model, testPrompt, cancellationToken),
                _ => new ConnectionTestResult(false, $"Unsupported provider: {provider}."),
            };
        }
        catch (OperationCanceledException)
        {
            return new ConnectionTestResult(false, "Timed out waiting for a response.");
        }
        catch (HttpRequestException ex)
        {
            return new ConnectionTestResult(false, ConnectionErrorFormatter.Describe(ex, serverUrl));
        }
        catch (JsonException ex)
        {
            return new ConnectionTestResult(false, $"Got a response, but couldn't parse it: {ex.Message}");
        }
    }

    private static async Task<ConnectionTestResult> TestOllamaAsync(HttpClient client, string serverUrl, string model, string testPrompt, CancellationToken cancellationToken)
    {
        var payload = new { model, prompt = testPrompt, stream = false };
        using var response = await client.PostAsJsonAsync(CombineUrl(serverUrl, "/api/generate"), payload, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return new ConnectionTestResult(false, $"Ollama returned {(int)response.StatusCode}: {Truncate(body)}");
        }

        using var document = JsonDocument.Parse(body);

        if (document.RootElement.TryGetProperty("error", out var errorElement))
        {
            return new ConnectionTestResult(false, $"Ollama error: {errorElement.GetString()}");
        }

        var text = document.RootElement.TryGetProperty("response", out var responseElement) ? responseElement.GetString() : null;

        return string.IsNullOrWhiteSpace(text)
            ? new ConnectionTestResult(false, "Ollama responded, but with an empty answer.")
            : new ConnectionTestResult(true, text.Trim());
    }

    private static async Task<ConnectionTestResult> TestLmStudioAsync(HttpClient client, string serverUrl, string model, string testPrompt, CancellationToken cancellationToken)
    {
        var payload = new { model, messages = new[] { new { role = "user", content = testPrompt } }, stream = false };
        using var response = await client.PostAsJsonAsync(CombineUrl(serverUrl, "/v1/chat/completions"), payload, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return new ConnectionTestResult(false, $"LM Studio returned {(int)response.StatusCode}: {Truncate(body)}");
        }

        using var document = JsonDocument.Parse(body);

        if (document.RootElement.TryGetProperty("error", out var errorElement))
        {
            var errorMessage = errorElement.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : errorElement.ToString();
            return new ConnectionTestResult(false, $"LM Studio error: {errorMessage}");
        }

        var text = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();

        return string.IsNullOrWhiteSpace(text)
            ? new ConnectionTestResult(false, "LM Studio responded, but with an empty answer.")
            : new ConnectionTestResult(true, text.Trim());
    }

    /// <summary>Ook gebruikt door ProviderStream: dezelfde adressen, dezelfde manier van plakken.</summary>
    internal static string CombineUrl(string serverUrl, string path) => serverUrl.TrimEnd('/') + path;

    private static string Truncate(string value) => value.Length > 300 ? value[..300] + "…" : value;
}
