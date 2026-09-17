using System.Data;
using System.Globalization;
using Jabasoft.Base.AiBroker;
using Jabasoft.Base.Logging;
using Microsoft.Data.SqlClient;

namespace Jabasoft.Broker;

/// <summary>
/// Schrijft het tokenverbruik van elke AI-aanroep weg in de gedeelde tabel
/// JabasoftBase.dbo.TokenUsageEntries, en leest het weer terug voor het
/// tokenoverzicht in de applicaties.
///
/// Waarom bij de broker: hij ziet elke aanroep van elke applicatie langs
/// komen, dus hier is het compleet zonder dat elke app zelf een
/// databaseverbinding nodig heeft. De apps vragen het overzicht via HTTP op.
///
/// Een log, geen tellers: een rij per aanroep, met tijdstip, applicatie en
/// model. Optellen doen we bij het uitlezen - dan kun je later ook per week,
/// per app of per model kijken zonder dat er iets anders bijgehouden hoeft
/// te worden.
///
/// Wegschrijven mag nooit een AI-aanroep laten mislukken: het antwoord is
/// al gegeven, het tellen is bijzaak. Een fout daarin wordt daarom
/// opgevangen en gemeld in het activiteitenlog, niet teruggegeven aan de
/// aanroeper.
/// </summary>
public sealed class TokenUsageStore(IConfiguration configuration, ActivityLog log)
{
    private string ConnectionString =>
        configuration.GetConnectionString("JabasoftBase")
        ?? throw new InvalidOperationException("Geen verbindingsreeks JabasoftBase in appsettings.json.");

    /// <summary>
    /// Is de database er, en staat de tabel erin? Dit is waar de
    /// gezondheidscontrole van de applicaties op leunt - vandaar dat het
    /// antwoord ook zegt WAT er mis is, en niet alleen dat er iets mis is.
    /// </summary>
    public async Task<TokenUsageStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var verbinding = new SqlConnection(ConnectionString);
            await verbinding.OpenAsync(cancellationToken);

            await using var opdracht = new SqlCommand(
                "SELECT COUNT(*) FROM sys.tables WHERE name = 'TokenUsageEntries'",
                verbinding);

            var gevonden = Convert.ToInt32(await opdracht.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);

            return gevonden > 0
                ? new TokenUsageStatus(true, null)
                : new TokenUsageStatus(false, "Tabel TokenUsageEntries ontbreekt in JabasoftBase.");
        }
        catch (Exception ex)
        {
            return new TokenUsageStatus(false, ex.Message);
        }
    }

    /// <summary>Legt een aanroep vast. Faalt dit, dan komt het in het activiteitenlog en gaat de aanroep gewoon door.</summary>
    public async Task RecordAsync(string application, string? model, long promptTokens, long completionTokens, CancellationToken cancellationToken)
    {
        // Niets te melden: een aanroep zonder tokens (mislukt, of een server
        // die geen aantallen teruggeeft) levert geen bruikbare regel op.
        if (promptTokens <= 0 && completionTokens <= 0)
        {
            return;
        }

        try
        {
            await using var verbinding = new SqlConnection(ConnectionString);
            await verbinding.OpenAsync(cancellationToken);

            await using var opdracht = new SqlCommand(
                """
                INSERT INTO dbo.TokenUsageEntries
                    (Id, Application, Timestamp, Model, PromptTokens, CompletionTokens, TotalTokens)
                VALUES
                    (@id, @application, @timestamp, @model, @prompt, @completion, @total)
                """,
                verbinding);

            opdracht.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
            opdracht.Parameters.Add("@application", SqlDbType.NVarChar, 100).Value =
                string.IsNullOrWhiteSpace(application) ? "onbekend" : application;
            opdracht.Parameters.Add("@timestamp", SqlDbType.DateTimeOffset).Value = DateTimeOffset.Now;
            opdracht.Parameters.Add("@model", SqlDbType.NVarChar, 200).Value = (object?)model ?? DBNull.Value;
            opdracht.Parameters.Add("@prompt", SqlDbType.BigInt).Value = promptTokens;
            opdracht.Parameters.Add("@completion", SqlDbType.BigInt).Value = completionTokens;
            opdracht.Parameters.Add("@total", SqlDbType.BigInt).Value = promptTokens + completionTokens;

            await opdracht.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            log.Add("broker", $"Tokenverbruik niet weggeschreven: {ex.Message}");
        }
    }

    /// <summary>
    /// Alle weken met een totaal, nieuwste eerst.
    ///
    /// Het groeperen gebeurt hier en niet in SQL: een ISO-week (maandag tot
    /// en met zondag, en de weeknummering rond de jaarwisseling) is in .NET
    /// met ISOWeek in één regel goed, en in T-SQL een reeks
    /// DATEPART-trucjes die je bij elke jaarwisseling opnieuw moet
    /// nakijken. Het is een logtabel van een werkplek; wordt hij ooit zo
    /// groot dat dit merkbaar wordt, dan verhuist het optellen naar SQL.
    /// </summary>
    public async Task<IReadOnlyList<TokenUsageWeek>> GetWeeksAsync(CancellationToken cancellationToken)
    {
        var regels = await LeesAsync(null, null, cancellationToken);

        return regels
            .GroupBy(regel => WeekSleutel(regel.Timestamp))
            .Select(groep =>
            {
                var eerste = groep.First().Timestamp;
                var maandag = ISOWeek.ToDateTime(ISOWeek.GetYear(eerste.LocalDateTime), ISOWeek.GetWeekOfYear(eerste.LocalDateTime), DayOfWeek.Monday);

                return new TokenUsageWeek(
                    groep.Key,
                    maandag,
                    maandag.AddDays(6),
                    groep.Sum(r => r.PromptTokens),
                    groep.Sum(r => r.CompletionTokens),
                    groep.Sum(r => r.TotalTokens),
                    groep.Count());
            })
            .OrderByDescending(week => week.Start)
            .ToList();
    }

    /// <summary>
    /// Het totaal per model, het zwaarste eerst. Over alles heen, niet per
    /// week: het tokenblok in de header laat zien waar je tokens structureel
    /// heen gaan, niet wat er deze week toevallig langskwam.
    /// </summary>
    public async Task<IReadOnlyList<TokenUsageModel>> GetModelsAsync(CancellationToken cancellationToken)
    {
        var regels = await LeesAsync(null, null, cancellationToken);

        return regels
            .GroupBy(regel => string.IsNullOrWhiteSpace(regel.Model) ? "onbekend" : regel.Model!, StringComparer.OrdinalIgnoreCase)
            .Select(groep => new TokenUsageModel(groep.Key, groep.Sum(r => r.TotalTokens), groep.Count()))
            .OrderByDescending(model => model.TotalTokens)
            .ToList();
    }

    /// <summary>De losse regels van één week, nieuwste eerst.</summary>
    public async Task<IReadOnlyList<TokenUsageEntry>> GetEntriesAsync(string week, CancellationToken cancellationToken)
    {
        if (!Ontleed(week, out var jaar, out var weeknummer))
        {
            return [];
        }

        var maandag = ISOWeek.ToDateTime(jaar, weeknummer, DayOfWeek.Monday);

        var regels = await LeesAsync(maandag, maandag.AddDays(7), cancellationToken);
        return regels.OrderByDescending(regel => regel.Timestamp).ToList();
    }

    private async Task<List<TokenUsageEntry>> LeesAsync(DateTime? vanaf, DateTime? tot, CancellationToken cancellationToken)
    {
        var regels = new List<TokenUsageEntry>();

        var sql = """
            SELECT Id, Application, Timestamp, Model, PromptTokens, CompletionTokens, TotalTokens
            FROM dbo.TokenUsageEntries
            """;

        if (vanaf is not null)
        {
            sql += " WHERE Timestamp >= @vanaf AND Timestamp < @tot";
        }

        sql += " ORDER BY Timestamp";

        try
        {
            await using var verbinding = new SqlConnection(ConnectionString);
            await verbinding.OpenAsync(cancellationToken);

            await using var opdracht = new SqlCommand(sql, verbinding);
            if (vanaf is not null)
            {
                opdracht.Parameters.Add("@vanaf", SqlDbType.DateTimeOffset).Value = new DateTimeOffset(vanaf.Value);
                opdracht.Parameters.Add("@tot", SqlDbType.DateTimeOffset).Value = new DateTimeOffset(tot!.Value);
            }

            await using var lezer = await opdracht.ExecuteReaderAsync(cancellationToken);
            while (await lezer.ReadAsync(cancellationToken))
            {
                regels.Add(new TokenUsageEntry(
                    lezer.GetGuid(0),
                    lezer.GetString(1),
                    lezer.GetDateTimeOffset(2),
                    lezer.IsDBNull(3) ? null : lezer.GetString(3),
                    lezer.GetInt64(4),
                    lezer.GetInt64(5),
                    lezer.GetInt64(6)));
            }
        }
        catch (Exception ex)
        {
            log.Add("broker", $"Tokenverbruik niet uit te lezen: {ex.Message}");
        }

        return regels;
    }

    /// <summary>"2026-W38" - sorteert vanzelf goed als tekst, en is leesbaar in een URL.</summary>
    private static string WeekSleutel(DateTimeOffset moment)
    {
        var lokaal = moment.LocalDateTime;
        return string.Create(CultureInfo.InvariantCulture, $"{ISOWeek.GetYear(lokaal)}-W{ISOWeek.GetWeekOfYear(lokaal):00}");
    }

    private static bool Ontleed(string week, out int jaar, out int weeknummer)
    {
        jaar = 0;
        weeknummer = 0;

        var delen = week?.Split('-');
        if (delen is not { Length: 2 } || !delen[1].StartsWith('W'))
        {
            return false;
        }

        return int.TryParse(delen[0], CultureInfo.InvariantCulture, out jaar)
            && int.TryParse(delen[1][1..], CultureInfo.InvariantCulture, out weeknummer);
    }
}
