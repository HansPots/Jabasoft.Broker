using System.ComponentModel;
using Jabasoft.Ai.Data;
using Jabasoft.Base.AiBroker;
using Jabasoft.Base.Logging;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Jabasoft.Broker;

/// <summary>
/// De tools die de broker via MCP (/mcp) aanbiedt aan AI-agents zoals Claude
/// Code en Hermes. Bewust alleen tools die LEZEN: een agent kan hiermee zien
/// wat er speelt, maar niets wijzigen, annuleren of starten. Acties die wel
/// iets doen komen er later pas bij, elk apart en expliciet.
///
/// Elke tool hergebruikt wat de broker al heeft (dezelfde stores als de
/// /api-endpoints), zodat er geen tweede waarheid ontstaat.
/// </summary>
[McpServerToolType]
internal sealed class McpTools(
    AiSettingsStore settings,
    RequestGate gate,
    AiWachtrij wachtrij,
    TokenUsageStore usage,
    ActivityLog log,
    IServiceScopeFactory scopes)
{
    private const string Toepassing = "MCP";

    [McpServerTool(Name = "broker_status", ReadOnly = true)]
    [Description("Status van de Jabasoft-broker: welke AI-server (LM Studio of Ollama) en welke modellen ingesteld staan, of de database bereikbaar is, en hoeveel vragen er nu in de wachtrij staan.")]
    public async Task<object> BrokerStatus(CancellationToken ct)
    {
        var huidig = settings.Current;
        var database = await usage.GetStatusAsync(ct);

        return new
        {
            Versie = typeof(McpTools).Assembly.GetName().Version?.ToString(),
            Provider = huidig.Provider.ToString(),
            ServerUrl = huidig.ActiveServerUrl,
            ChatModel = huidig.Active.ChatModel,
            EmbeddingModel = huidig.Active.EmbedModel,
            Database = database,
            VragenInWachtrij = wachtrij.Lijst().Count,
        };
    }

    [McpServerTool(Name = "modellen", ReadOnly = true)]
    [Description("De modellen die op dit moment aanwezig zijn op de ingestelde AI-server (LM Studio of Ollama).")]
    public async Task<object> Modellen(CancellationToken ct)
    {
        var huidig = settings.Current;
        return await gate.GetModelsAsync(huidig.Provider, huidig.ActiveServerUrl, ct);
    }

    [McpServerTool(Name = "wachtrij", ReadOnly = true)]
    [Description("Wat er nu aan AI-vragen loopt, wacht en net klaar is, van alle Jabasoft-apps samen.")]
    public object Wachtrij() => wachtrij.Lijst();

    [McpServerTool(Name = "verbruik", ReadOnly = true)]
    [Description("Tokenverbruik van de Jabasoft-apps: totalen per week (nieuwste eerst), per model, en per onderdeel van een app over de laatste N dagen.")]
    public async Task<object> Verbruik(
        [Description("Over hoeveel dagen het verbruik per onderdeel geteld wordt (standaard 7).")] int dagen = 7,
        CancellationToken ct = default)
    {
        return new
        {
            Weken = await usage.GetWeeksAsync(ct),
            Modellen = await usage.GetModelsAsync(ct),
            Onderdelen = await usage.GetOnderdelenAsync(Math.Clamp(dagen, 1, 365), ct),
        };
    }

    [McpServerTool(Name = "zoek", ReadOnly = true)]
    [Description("Zoekt semantisch (op betekenis) in de Jabasoft-zoekindex van één project. Laat 'project' leeg om eerst te zien welke projecten er geïndexeerd zijn.")]
    public async Task<object> Zoek(
        [Description("Het project om in te zoeken, precies zoals de lijst het noemt. Leeg = geef de lijst met projecten.")] string? project = null,
        [Description("Waar je naar zoekt, in gewone taal.")] string? vraag = null,
        [Description("Maximaal aantal treffers (standaard 5, hoogstens 20).")] int maxResultaten = 5,
        CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();

        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(vraag))
        {
            var db = scope.ServiceProvider.GetRequiredService<AiDbContext>();
            var projecten = await db.SearchDocuments
                .GroupBy(d => d.Project)
                .Select(g => new { Project = g.Key, Bestanden = g.Count() })
                .OrderBy(p => p.Project)
                .ToListAsync(ct);

            return new { Uitleg = "Geef een project én een vraag mee om te zoeken.", Projecten = projecten };
        }

        var huidig = settings.Current;
        if (string.IsNullOrWhiteSpace(huidig.Active.EmbedModel))
        {
            return new SemanticSearchResult(false, [], "Er staat geen embeddingmodel ingesteld (Jabasoft, AI-instellingen).");
        }

        var index = scope.ServiceProvider.GetRequiredService<SearchIndexStore>();
        return await index.SearchAsync(
            new SemanticSearchRequest(
                huidig.Provider,
                huidig.ActiveServerUrl,
                huidig.Active.EmbedModel,
                project,
                vraag,
                Toepassing,
                Math.Clamp(maxResultaten, 1, 20)),
            ct);
    }

    [McpServerTool(Name = "logs", ReadOnly = true)]
    [Description("De recente activiteit van de broker (aanroepen, fouten, instellingswijzigingen), nieuwste laatst.")]
    public object Logs(
        [Description("Hoeveel regels (standaard 50, hoogstens 500).")] int aantal = 50)
    {
        var alles = log.Since(0);
        return alles.Skip(Math.Max(0, alles.Count - Math.Clamp(aantal, 1, 500))).ToList();
    }
}
