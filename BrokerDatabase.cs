using Jabasoft.Ai.Data;
using Jabasoft.Base.Logging;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Jabasoft.Broker;

/// <summary>
/// De ene database van de broker: JabasoftBroker. Vervangt de twee oude
/// (JabasoftBase voor het tokenverbruik, JabasoftAi voor de zoekindex).
///
/// Bij het opstarten:
/// 1. de EF-migraties (maakt de database en SearchDocuments aan als ze er nog niet zijn);
/// 2. de tokentabel aanmaken - bij de eerste keer met alle regels uit JabasoftBase;
/// 3. de zoekindex één keer overnemen uit JabasoftAi.
///
/// Wat al overgezet is, staat in dbo.Overgezet, zodat het nooit twee keer
/// gebeurt. De oude databases blijven onaangeroerd staan als reservekopie.
/// Dit draait in de broker zelf (en niet als los script), omdat LocalDB
/// alleen bereikbaar is vanuit de sessie van de gebruiker.
/// </summary>
internal static class BrokerDatabase
{
    public const string Naam = "JabasoftBroker";

    public static string Verbinding(IConfiguration configuration) =>
        configuration.GetConnectionString(Naam)
        ?? throw new InvalidOperationException($"ConnectionStrings:{Naam} ontbreekt in appsettings.json.");

    public static void Voorbereiden(IServiceProvider diensten, IConfiguration configuration, ActivityLog log)
    {
        diensten.GetRequiredService<AiDbContext>().Database.Migrate();

        try
        {
            using var verbinding = new SqlConnection(Verbinding(configuration));
            verbinding.Open();

            Voer(verbinding, """
                IF OBJECT_ID('dbo.Overgezet') IS NULL
                CREATE TABLE dbo.Overgezet (
                    Onderdeel    NVARCHAR(100) NOT NULL CONSTRAINT PK_Overgezet PRIMARY KEY,
                    Bron         NVARCHAR(200) NOT NULL,
                    Rijen        INT NOT NULL,
                    CreatedAtUtc DATETIME2 NOT NULL CONSTRAINT DF_Overgezet_Created DEFAULT SYSUTCDATETIME(),
                    UpdatedAtUtc DATETIME2 NOT NULL CONSTRAINT DF_Overgezet_Updated DEFAULT SYSUTCDATETIME())
                """);

            Tokentabel(verbinding, log);
            Zoekindex(verbinding, log);
        }
        catch (Exception fout)
        {
            // De broker moet ook zonder overzetting kunnen draaien: de tokentabel
            // meldt dan zelf dat hij ontbreekt, en zoeken vindt nog niets.
            log.Add("broker", $"Database {Naam} niet volledig voorbereid: {fout.Message}");
        }
    }

    private static void Tokentabel(SqlConnection verbinding, ActivityLog log)
    {
        if (Bestaat(verbinding, "SELECT OBJECT_ID('dbo.TokenUsageEntries')"))
        {
            return;
        }

        var bronAanwezig = Bestaat(verbinding, "SELECT OBJECT_ID('JabasoftBase.dbo.TokenUsageEntries')");
        if (bronAanwezig)
        {
            // SELECT INTO neemt de kolommen over zoals ze in de oude tabel staan.
            Voer(verbinding, "SELECT * INTO dbo.TokenUsageEntries FROM JabasoftBase.dbo.TokenUsageEntries");
        }
        else
        {
            Voer(verbinding, """
                CREATE TABLE dbo.TokenUsageEntries (
                    Id               UNIQUEIDENTIFIER NOT NULL,
                    Application      NVARCHAR(100) NOT NULL,
                    Model            NVARCHAR(200) NULL,
                    Onderdeel        NVARCHAR(100) NULL,
                    PromptTokens     BIGINT NOT NULL,
                    CompletionTokens BIGINT NOT NULL,
                    TotalTokens      BIGINT NOT NULL,
                    Timestamp        DATETIMEOFFSET NOT NULL)
                """);
        }

        Voer(verbinding, "IF COL_LENGTH('dbo.TokenUsageEntries', 'Onderdeel') IS NULL ALTER TABLE dbo.TokenUsageEntries ADD Onderdeel NVARCHAR(100) NULL");
        Voer(verbinding, "ALTER TABLE dbo.TokenUsageEntries ADD CONSTRAINT PK_TokenUsageEntries PRIMARY KEY (Id)");
        Voer(verbinding, """
            ALTER TABLE dbo.TokenUsageEntries ADD
                CreatedAtUtc DATETIME2 NOT NULL CONSTRAINT DF_TokenUsageEntries_Created DEFAULT SYSUTCDATETIME(),
                UpdatedAtUtc DATETIME2 NOT NULL CONSTRAINT DF_TokenUsageEntries_Updated DEFAULT SYSUTCDATETIME()
            """);
        Voer(verbinding, """
            UPDATE dbo.TokenUsageEntries
            SET CreatedAtUtc = CONVERT(DATETIME2, SWITCHOFFSET(CONVERT(DATETIMEOFFSET, Timestamp), '+00:00')),
                UpdatedAtUtc = CONVERT(DATETIME2, SWITCHOFFSET(CONVERT(DATETIMEOFFSET, Timestamp), '+00:00'))
            """);

        var rijen = Tel(verbinding, "SELECT COUNT(*) FROM dbo.TokenUsageEntries");
        Noteer(verbinding, "TokenUsageEntries", bronAanwezig ? "JabasoftBase" : "nieuw", rijen);
        log.Add("broker", bronAanwezig
            ? $"Tokenverbruik overgezet naar {Naam}: {rijen} regels uit JabasoftBase"
            : $"Lege tokentabel aangemaakt in {Naam}");
    }

    private static void Zoekindex(SqlConnection verbinding, ActivityLog log)
    {
        if (Bestaat(verbinding, "SELECT 1 FROM dbo.Overgezet WHERE Onderdeel = 'SearchDocuments'"))
        {
            return;
        }

        var bronAanwezig = Bestaat(verbinding, "SELECT OBJECT_ID('JabasoftAi.dbo.SearchDocuments')");
        if (bronAanwezig && !Bestaat(verbinding, "SELECT TOP 1 1 FROM dbo.SearchDocuments"))
        {
            Voer(verbinding, """
                SET IDENTITY_INSERT dbo.SearchDocuments ON;
                INSERT INTO dbo.SearchDocuments (Id, Project, FilePath, Model, Content, Embedding, CreatedAtUtc, UpdatedAtUtc)
                SELECT Id, Project, FilePath, Model, Content, Embedding, CreatedAtUtc, UpdatedAtUtc
                FROM JabasoftAi.dbo.SearchDocuments;
                SET IDENTITY_INSERT dbo.SearchDocuments OFF;
                """);
        }

        var rijen = Tel(verbinding, "SELECT COUNT(*) FROM dbo.SearchDocuments");
        Noteer(verbinding, "SearchDocuments", bronAanwezig ? "JabasoftAi" : "nieuw", rijen);
        if (bronAanwezig)
        {
            log.Add("broker", $"Zoekindex overgezet naar {Naam}: {rijen} documenten uit JabasoftAi");
        }
    }

    private static void Noteer(SqlConnection verbinding, string onderdeel, string bron, int rijen)
    {
        using var opdracht = new SqlCommand(
            "INSERT INTO dbo.Overgezet (Onderdeel, Bron, Rijen) VALUES (@onderdeel, @bron, @rijen)", verbinding);
        opdracht.Parameters.AddWithValue("@onderdeel", onderdeel);
        opdracht.Parameters.AddWithValue("@bron", bron);
        opdracht.Parameters.AddWithValue("@rijen", rijen);
        opdracht.ExecuteNonQuery();
    }

    private static void Voer(SqlConnection verbinding, string sql)
    {
        using var opdracht = new SqlCommand(sql, verbinding) { CommandTimeout = 600 };
        opdracht.ExecuteNonQuery();
    }

    private static bool Bestaat(SqlConnection verbinding, string sql)
    {
        using var opdracht = new SqlCommand(sql, verbinding);
        return opdracht.ExecuteScalar() is not (null or DBNull);
    }

    private static int Tel(SqlConnection verbinding, string sql)
    {
        using var opdracht = new SqlCommand(sql, verbinding);
        return Convert.ToInt32(opdracht.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
