using System.Collections.Concurrent;
using Jabasoft.Base.AiBroker;

namespace Jabasoft.Broker;

/// <summary>
/// The "wachtrij" (queue) and the "check bereikbaarheid eenmalig" (check
/// reachability once) parts of the broker.
///
/// Concurrency, per (provider, serverUrl):
/// - Staat er niets te lopen, dan mag een vraag altijd door. Die eerste vraag
///   mag het model laden, herladen of andere modellen uit het geheugen halen.
/// - Loopt er al iets met HETZELFDE model, dan mag een volgende vraag meteen
///   mee, zolang het model dat aankan: bij LM Studio het aantal gelijktijdige
///   voorspellingen van het geladen model ("Max Concurrent", `parallel` in
///   `lms ps`), anders Broker:MaxConcurrentPerServer (1).
/// - Een vraag voor een ANDER model, of een die alles voor zich alleen nodig
///   heeft (tekenen: alle modellen eruit), wacht tot er niets meer loopt. Zolang
///   zo'n vraag wacht, schuiven er geen nieuwe meelopers aan, anders komt hij
///   nooit aan de beurt.
/// - Een model herladen mag alleen wie er alleen op zit (zie
///   <see cref="ProbeerHerlaadrecht"/>); zo haalt LmStudioContext nooit een model
///   weg onder een vraag die nog loopt. Heeft een meeloper meer context nodig
///   dan er geladen staat, dan gaat hij terug in de rij en wacht hij tot hij
///   alleen is (<see cref="AlleenNodigException"/>).
///
/// Reachability/model list: cached per (provider, serverUrl) for a short
/// TTL (Broker:ReachabilityCacheSeconds) - the model-list call doubles as
/// the reachability probe, so multiple apps asking around the same time
/// share one cached result instead of each hitting the LLM server.
/// </summary>
internal sealed class RequestGate(ProviderClient providerClient, IConfiguration configuration)
{
    /// <summary>Statisch: LmStudioContext (ook statisch) moet kunnen vragen wie er op een model zit.</summary>
    private static readonly ConcurrentDictionary<string, Server> Servers = new();

    private readonly ConcurrentDictionary<string, (DateTimeOffset FetchedAt, ModelListResult Result)> _modelCache = new();

    private int MaxConcurrentPerServer => configuration.GetValue("Broker:MaxConcurrentPerServer", 1);

    /// <summary>De toestand van één server: wat er loopt en met welk model.</summary>
    private sealed class Server
    {
        public readonly Lock Slot = new();

        /// <summary>Het model van de lopende vragen; null als er niets loopt of als de lopende vraag alles nodig heeft.</summary>
        public string? Model;

        public int Lopend;

        /// <summary>De lopende vraag heeft de server voor zich alleen (tekenen, of een meeloper die moest herladen).</summary>
        public bool Alleen;

        /// <summary>Er wordt nu een model geladen of herladen: dan schuift er niemand aan.</summary>
        public bool Herladen;

        /// <summary>Vragen die wachten tot er niets meer loopt; zolang die er zijn, geen nieuwe meelopers.</summary>
        public int WachtendOpLeeg;

        public TaskCompletionSource Veranderd = NieuwSignaal();

        public void Meld()
        {
            Veranderd.TrySetResult();
            Veranderd = NieuwSignaal();
        }

        private static TaskCompletionSource NieuwSignaal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Een meeloper had meer context nodig dan er geladen staat; hij moet het opnieuw proberen als hij alleen is.</summary>
    internal sealed class AlleenNodigException() : Exception("Deze vraag moet wachten tot het model vrij is om te herladen.");

    /// <summary>
    /// Mag de aanroeper <paramref name="model"/> nu laden of herladen? Ja als er niemand anders op dat model zit
    /// (of als het buiten de wachtrij om gevraagd wordt). Het resultaat moet na het laden worden losgelaten (Dispose):
    /// zolang het vastgehouden wordt, schuift er niemand aan. Null = nee, er lopen nog andere vragen op dit model.
    /// </summary>
    public static IDisposable? ProbeerHerlaadrecht(string model)
    {
        foreach (var server in Servers.Values)
        {
            lock (server.Slot)
            {
                if (server.Lopend == 0 || !string.Equals(server.Model, model, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (server.Lopend > 1)
                {
                    return null;
                }

                server.Herladen = true;
                return new Herlaadrecht(server);
            }
        }

        return new Herlaadrecht(null);
    }

    private sealed class Herlaadrecht(Server? server) : IDisposable
    {
        private Server? _server = server;

        public void Dispose()
        {
            var server = Interlocked.Exchange(ref _server, null);
            if (server is null)
            {
                return;
            }

            lock (server.Slot)
            {
                server.Herladen = false;
                server.Meld();
            }
        }
    }

    /// <summary>
    /// Wacht tot deze vraag aan de beurt is. <paramref name="model"/> null of <paramref name="alleen"/>: wachten
    /// tot er niets meer loopt. Geeft terug of de vraag als meeloper binnenkwam.
    /// </summary>
    private async Task<bool> BinnenAsync(AiProvider provider, string serverUrl, string? model, bool alleen, CancellationToken cancellationToken)
    {
        var server = Servers.GetOrAdd(Key(provider, serverUrl), _ => new Server());
        var moetLeeg = alleen || model is null;
        var telAlsWachtend = false;

        try
        {
            while (true)
            {
                Task wacht;
                var probeerMee = false;

                lock (server.Slot)
                {
                    if (server.Lopend == 0)
                    {
                        server.Lopend = 1;
                        server.Model = model;
                        server.Alleen = moetLeeg;
                        return false;
                    }

                    if (!moetLeeg && KanMee(server, model!))
                    {
                        probeerMee = true;
                    }
                    else if (moetLeeg || !string.Equals(server.Model, model, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!telAlsWachtend)
                        {
                            telAlsWachtend = true;
                            server.WachtendOpLeeg++;
                        }
                    }

                    wacht = server.Veranderd.Task;
                }

                if (probeerMee)
                {
                    // Buiten het slot: `lms ps` duurt even.
                    var parallel = await ParallelAsync(provider, model!, cancellationToken);

                    lock (server.Slot)
                    {
                        if (server.Lopend == 0)
                        {
                            continue;
                        }

                        if (KanMee(server, model!) && server.Lopend < parallel)
                        {
                            server.Lopend++;
                            return true;
                        }

                        wacht = server.Veranderd.Task;
                    }
                }

                await wacht.WaitAsync(cancellationToken);
            }
        }
        finally
        {
            if (telAlsWachtend)
            {
                lock (server.Slot)
                {
                    server.WachtendOpLeeg--;
                    server.Meld();
                }
            }
        }
    }

    private static bool KanMee(Server server, string model) =>
        !server.Alleen && !server.Herladen && server.WachtendOpLeeg == 0 &&
        string.Equals(server.Model, model, StringComparison.OrdinalIgnoreCase);

    private void Buiten(AiProvider provider, string serverUrl)
    {
        var server = Servers.GetOrAdd(Key(provider, serverUrl), _ => new Server());
        lock (server.Slot)
        {
            server.Lopend--;
            if (server.Lopend <= 0)
            {
                server.Lopend = 0;
                server.Model = null;
                server.Alleen = false;
            }

            server.Meld();
        }
    }

    /// <summary>Hoeveel vragen het model tegelijk aankan. Alleen LM Studio vertelt dat; anders de instelling (1).</summary>
    private async Task<int> ParallelAsync(AiProvider provider, string model, CancellationToken cancellationToken)
    {
        if (provider == AiProvider.LmStudio && await LmStudioContext.ParallelAsync(model, cancellationToken) is { } parallel)
        {
            return parallel;
        }

        return MaxConcurrentPerServer;
    }

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

    /// <param name="model">Het model van deze vraag; null = de server voor zich alleen (tekenen).</param>
    /// <param name="opBeurt">Gaat af op het moment dat deze aanroep aan de beurt is - voor de wachtrij (zie AiWachtrij).</param>
    public async Task<T> RunGatedAsync<T>(AiProvider provider, string serverUrl, string? model, Func<Task<T>> action, CancellationToken cancellationToken, Action? opBeurt = null)
    {
        var alleen = false;

        while (true)
        {
            await BinnenAsync(provider, serverUrl, model, alleen, cancellationToken);
            try
            {
                opBeurt?.Invoke();
                return await action();
            }
            catch (AlleenNodigException) when (!alleen)
            {
                // Er is nog niets teruggegeven: opnieuw, zodra deze vraag alleen is en mag herladen.
                alleen = true;
            }
            finally
            {
                Buiten(provider, serverUrl);
            }
        }
    }

    /// <summary>
    /// Hetzelfde wachten op je beurt, maar voor een antwoord dat stukje
    /// bij beetje binnenkomt: de beurt wordt vastgehouden zolang de
    /// stroom loopt en pas losgelaten als hij afgelopen is. Zou dat niet
    /// zo zijn, dan telde de broker een lopende vraag niet mee en kon hij
    /// een model herladen of verwisselen terwijl er nog een antwoord komt.
    /// </summary>
    public async IAsyncEnumerable<T> RunGatedStreamAsync<T>(
        AiProvider provider,
        string serverUrl,
        string? model,
        Func<IAsyncEnumerable<T>> action,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
        Action? opBeurt = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        var alleen = false;

        while (true)
        {
            await BinnenAsync(provider, serverUrl, model, alleen, cancellationToken);
            var opnieuw = false;
            try
            {
                opBeurt?.Invoke();
                var stroom = action().WithCancellation(cancellationToken).GetAsyncEnumerator();
                await using (stroom)
                {
                    var iets = false;
                    while (true)
                    {
                        // Een yield mag niet in een try met catch staan, dus eerst het volgende stukje ophalen.
                        bool heeft;
                        try
                        {
                            heeft = await stroom.MoveNextAsync();
                        }
                        catch (AlleenNodigException) when (!alleen && !iets)
                        {
                            // Nog niets teruggegeven: opnieuw, zodra deze vraag alleen is en mag herladen.
                            opnieuw = true;
                            break;
                        }

                        if (!heeft)
                        {
                            break;
                        }

                        iets = true;
                        yield return stroom.Current;
                    }
                }
            }
            finally
            {
                Buiten(provider, serverUrl);
            }

            if (!opnieuw)
            {
                yield break;
            }

            alleen = true;
        }
    }

    private static string Key(AiProvider provider, string serverUrl) => $"{provider}:{serverUrl.TrimEnd('/')}";
}
