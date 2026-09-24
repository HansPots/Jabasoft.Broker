using System.Text.Json;
using Jabasoft.Ai.Data;
using Jabasoft.Ai.Data.Entities;
using Jabasoft.Base.AiBroker;
using Jabasoft.Base.Logging;
using Microsoft.EntityFrameworkCore;

namespace Jabasoft.Broker;

/// <summary>
/// Indexeert projectbestanden voor semantisch zoeken, en zoekt erin.
/// Praat met de AI-server via <see cref="ProviderClient"/> (dezelfde weg
/// als /api/embed) en schrijft weg in de AI-database (JabasoftAi, via
/// Jabasoft.Ai.Data).
///
/// EERSTE, SIMPELE opzet: het vector wordt als JSON-tekst bewaard en de
/// vergelijking gebeurt hier in C#, niet met het native VECTOR-type van
/// SQL Server 2025. Voor een project van een paar honderd bestanden is
/// dat ruim snel genoeg; wordt dat ooit te traag, dan is de volgende stap
/// een VECTOR-kolom met VECTOR_DISTANCE in T-SQL - een migratie op
/// dezelfde tabel, geen nieuw ontwerp.
/// </summary>
internal sealed class SearchIndexStore(AiDbContext db, ProviderClient client, TokenUsageStore usage, ActivityLog log, AiSettingsStore settings)
{
    /// <summary>Hoeveel tekst er per bestand bewaard blijft voor het fragment bij een treffer - een heel bestand hoeft niet dubbel opgeslagen te worden.</summary>
    private const int MaxBewaardeTekst = 4000;

    /// <summary>Hoeveel tekens van het fragment daadwerkelijk teruggaan naar de aanroeper.</summary>
    private const int FragmentLengte = 300;

    /// <summary>
    /// Onder deze cosinus-gelijkenis is een treffer geen antwoord meer,
    /// alleen nog ruis - zonder grens komt semantisch zoeken NOOIT met
    /// niets terug, want er is altijd wel een "minst slechte" match.
    ///
    /// Komt uit de AI-instelling (AiSettings.MinimumSemanticScore) en dus
    /// niet uit appsettings.json: het is dezelfde soort instelling als het
    /// chat- en embeddingmodel - een voor de hele machine, aan te passen
    /// vanuit Jabasoft (Controls/Setting-06) zonder rebuild.
    /// </summary>
    private double MinimumGelijkenis => settings.Current.MinimumSemanticScore;

    /// <summary>
    /// (Her)indexeert de meegegeven bestanden: elk wordt geëmbed en als
    /// rij weggeschreven (nieuw, of de bestaande rij bijgewerkt). Een leeg
    /// bestand of een embedding die mislukt telt als overgeslagen, niet
    /// als fout - de rest van de bestanden moet gewoon doorgaan.
    /// </summary>
    public async Task<SearchIndexResult> IndexAsync(SearchIndexRequest request, CancellationToken cancellationToken)
    {
        var geindexeerd = 0;
        var overgeslagen = 0;

        foreach (var bestand in request.Files)
        {
            if (string.IsNullOrWhiteSpace(bestand.Content))
            {
                overgeslagen++;
                continue;
            }

            var resultaat = await client.EmbedAsync(request.Provider, request.ServerUrl, request.Model, bestand.Content, cancellationToken);

            if (!resultaat.Success)
            {
                overgeslagen++;
                continue;
            }

            await usage.RecordAsync(request.Application, request.Model, resultaat.TokensUsed, 0, cancellationToken);

            var bewaardeTekst = bestand.Content.Length > MaxBewaardeTekst
                ? bestand.Content[..MaxBewaardeTekst]
                : bestand.Content;

            var bestaand = await db.SearchDocuments.SingleOrDefaultAsync(
                d => d.Project == request.Project && d.FilePath == bestand.Path,
                cancellationToken);

            if (bestaand is null)
            {
                db.SearchDocuments.Add(new SearchDocument
                {
                    Project = request.Project,
                    FilePath = bestand.Path,
                    Model = request.Model,
                    Content = bewaardeTekst,
                    Embedding = JsonSerializer.Serialize(resultaat.Vector),
                });
            }
            else
            {
                bestaand.Model = request.Model;
                bestaand.Content = bewaardeTekst;
                bestaand.Embedding = JsonSerializer.Serialize(resultaat.Vector);
            }

            geindexeerd++;
        }

        await db.SaveChangesAsync(cancellationToken);

        log.Add(
            "broker",
            overgeslagen > 0
                ? $"Zoekindex bijgewerkt voor {request.Project}: {geindexeerd} bestanden, {overgeslagen} overgeslagen"
                : $"Zoekindex bijgewerkt voor {request.Project}: {geindexeerd} bestanden");

        return new SearchIndexResult(true, geindexeerd, overgeslagen, null);
    }

    /// <summary>
    /// Embedt elk al geïndexeerd bestand OPNIEUW met het meegegeven model -
    /// de tekst zelf hoeft niet opnieuw van schijf gelezen te worden, die
    /// staat al in <see cref="SearchDocument.Content"/>. Wordt aangeroepen
    /// als het embeddingmodel (of de server erachter) wijzigt: een vector
    /// van het oude model is niet vergelijkbaar met een vraag die met het
    /// nieuwe model geëmbed wordt, dus dan is elke bestaande rij verouderd.
    ///
    /// Draait op de achtergrond (zie Program.cs) - dit kan bij veel
    /// geïndexeerde bestanden een tijdje duren, en de instelling zelf is
    /// dan al opgeslagen. Tussentijds bewaren (elke 25) zodat een server
    /// die halverwege wegvalt niet al het werk weggooit.
    /// </summary>
    public async Task ReindexAllAsync(AiProvider provider, string serverUrl, string model, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        var documenten = await db.SearchDocuments.ToListAsync(cancellationToken);

        if (documenten.Count == 0)
        {
            return;
        }

        log.Add("broker", $"Zoekindex: embeddingmodel gewijzigd naar '{model}' - {documenten.Count} bestanden opnieuw indexeren…");

        var gelukt = 0;
        var mislukt = 0;

        foreach (var document in documenten)
        {
            var resultaat = await client.EmbedAsync(provider, serverUrl, model, document.Content, cancellationToken);

            if (!resultaat.Success)
            {
                mislukt++;
                continue;
            }

            await usage.RecordAsync("Broker", model, resultaat.TokensUsed, 0, cancellationToken);

            document.Model = model;
            document.Embedding = JsonSerializer.Serialize(resultaat.Vector);
            gelukt++;

            if (gelukt % 25 == 0)
            {
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        log.Add(
            "broker",
            mislukt > 0
                ? $"Zoekindex bijgewerkt: {gelukt} bestanden opnieuw geïndexeerd, {mislukt} mislukt"
                : $"Zoekindex bijgewerkt: {gelukt} bestanden opnieuw geïndexeerd");
    }

    /// <summary>
    /// Embedt de vraag en vergelijkt hem met alles wat voor dit project
    /// geïndexeerd staat. Geen index? Dan komt er gewoon een lege lijst
    /// terug - dat is geen fout, dat is "nog niet geïndexeerd".
    ///
    /// Hoeveel er teruggaan volgt <see cref="SemanticSearchRequest.MaxResults"/>:
    /// zijn er MEER treffers dan dat die de ondergrens halen
    /// (<see cref="MinimumGelijkenis"/>), dan gaan die ALLEMAAL mee. Zijn
    /// er minder, dan wordt er aangevuld met de beste van de rest tot dat
    /// aantal - ook al halen die de grens niet. Elke treffer draagt zelf
    /// of hij de grens haalt (<see cref="SemanticSearchHit.MeetsThreshold"/>),
    /// zodat de aanroeper het verschil kan laten zien in plaats van dat de
    /// broker maar een deel van de waarheid vertelt.
    /// </summary>
    public async Task<SemanticSearchResult> SearchAsync(SemanticSearchRequest request, CancellationToken cancellationToken)
    {
        var vraag = await client.EmbedAsync(request.Provider, request.ServerUrl, request.Model, request.Query, cancellationToken);

        if (!vraag.Success)
        {
            return new SemanticSearchResult(false, [], vraag.ErrorMessage);
        }

        await usage.RecordAsync(request.Application, request.Model, vraag.TokensUsed, 0, cancellationToken);

        var documenten = await db.SearchDocuments
            .Where(d => d.Project == request.Project)
            .Select(d => new { d.FilePath, d.Content, d.Embedding })
            .ToListAsync(cancellationToken);

        var gescoord = documenten
            .Select(document => new
            {
                document.FilePath,
                document.Content,
                Score = CosinusGelijkenis(vraag.Vector, JsonSerializer.Deserialize<float[]>(document.Embedding) ?? []),
            })
            .OrderByDescending(document => document.Score)
            .ToList();

        var minimumAantal = Math.Max(1, request.MaxResults);
        var haaltDeGrens = gescoord.Where(document => document.Score >= MinimumGelijkenis).ToList();

        // Meer echte treffers dan het minimum? Dan die allemaal - niet
        // afkappen om een rond getal. Anders het minimum vol maken met de
        // beste van de rest (die zijn al bovenaan gesorteerd), ook al
        // halen die de grens niet.
        var gekozen = haaltDeGrens.Count > minimumAantal ? haaltDeGrens : gescoord.Take(minimumAantal);

        var treffers = gekozen
            .Select(document => new SemanticSearchHit(
                document.FilePath,
                document.Content.Length > FragmentLengte ? document.Content[..FragmentLengte] + "…" : document.Content,
                document.Score,
                document.Score >= MinimumGelijkenis))
            .ToList();

        return new SemanticSearchResult(true, treffers, null);
    }

    /// <summary>
    /// De cosinus-gelijkenis van twee vectoren: 1 is identiek, 0 heeft
    /// niets met elkaar te maken. De gangbare maat voor embeddings, want
    /// die kijkt naar de RICHTING van de vectoren en niet naar hun
    /// lengte - twee stukken tekst over hetzelfde onderwerp wijzen dezelfde
    /// kant op, ook als het ene stuk veel langer is dan het andere.
    /// </summary>
    private static double CosinusGelijkenis(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length)
        {
            return 0;
        }

        double punt = 0;
        double lengteA = 0;
        double lengteB = 0;

        for (var i = 0; i < a.Length; i++)
        {
            punt += a[i] * b[i];
            lengteA += a[i] * a[i];
            lengteB += b[i] * b[i];
        }

        if (lengteA == 0 || lengteB == 0)
        {
            return 0;
        }

        return punt / (Math.Sqrt(lengteA) * Math.Sqrt(lengteB));
    }
}
