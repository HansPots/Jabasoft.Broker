using System.Collections.Concurrent;
using Jabasoft.Base.AiBroker;

namespace Jabasoft.Broker;

/// <summary>
/// The "wachtrij" (queue) and the "check bereikbaarheid eenmalig" (check
/// reachability once) parts of the broker.
///
/// Concurrency: one <see cref="SemaphoreSlim"/> per (provider, serverUrl)
/// pair, default capacity from Broker:MaxConcurrentPerServer (1) - a chat
/// or embed call from any app waits its turn if another app's call to the
/// *same* server is already in flight; calls to a different server are
/// unaffected. This is what actually serializes concurrent requests
/// instead of letting them hit the local LLM server at the same time.
///
/// Reachability/model list: cached per (provider, serverUrl) for a short
/// TTL (Broker:ReachabilityCacheSeconds) - the model-list call doubles as
/// the reachability probe, so multiple apps asking around the same time
/// share one cached result instead of each hitting the LLM server.
/// </summary>
internal sealed class RequestGate(ProviderClient providerClient, IConfiguration configuration)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset FetchedAt, ModelListResult Result)> _modelCache = new();

    private int MaxConcurrentPerServer => configuration.GetValue("Broker:MaxConcurrentPerServer", 1);

    private TimeSpan ReachabilityCacheTtl => TimeSpan.FromSeconds(configuration.GetValue("Broker:ReachabilityCacheSeconds", 15));

    public async Task<ModelListResult> GetModelsAsync(AiProvider provider, string serverUrl, CancellationToken cancellationToken)
    {
        var key = Key(provider, serverUrl);

        if (_modelCache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.FetchedAt < ReachabilityCacheTtl)
        {
            return cached.Result;
        }

        var result = await providerClient.ListModelsAsync(provider, serverUrl, cancellationToken);
        _modelCache[key] = (DateTimeOffset.UtcNow, result);
        return result;
    }

    public async Task<T> RunGatedAsync<T>(AiProvider provider, string serverUrl, Func<Task<T>> action, CancellationToken cancellationToken)
    {
        var semaphore = _semaphores.GetOrAdd(Key(provider, serverUrl), _ => new SemaphoreSlim(MaxConcurrentPerServer, MaxConcurrentPerServer));
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Hetzelfde wachten op je beurt, maar voor een antwoord dat stukje
    /// bij beetje binnenkomt: de beurt wordt vastgehouden zolang de
    /// stroom loopt en pas losgelaten als hij afgelopen is. Zou dat niet
    /// zo zijn, dan begon een tweede aanroep midden in de eerste en
    /// krijgen twee vragen tegelijk de lokale server te pakken.
    /// </summary>
    public async IAsyncEnumerable<T> RunGatedStreamAsync<T>(
        AiProvider provider,
        string serverUrl,
        Func<IAsyncEnumerable<T>> action,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        var semaphore = _semaphores.GetOrAdd(Key(provider, serverUrl), _ => new SemaphoreSlim(MaxConcurrentPerServer, MaxConcurrentPerServer));
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            await foreach (var stukje in action().WithCancellation(cancellationToken))
            {
                yield return stukje;
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static string Key(AiProvider provider, string serverUrl) => $"{provider}:{serverUrl.TrimEnd('/')}";
}
