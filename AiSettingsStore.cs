using System.Text.Json;
using System.Text.Json.Serialization;
using Jabasoft.Base.AiBroker;

namespace Jabasoft.Broker;

/// <summary>
/// De ene AI-instelling van deze machine, bewaard door de broker.
///
/// Waarom hier en niet in elke app apart: de broker is de gedeelde
/// voorziening waar alle applicaties langs gaan. Zet je het model hier om,
/// dan werkt elke app er meteen mee - er valt niets te synchroniseren,
/// want er is maar een plek waar het staat.
///
/// Het bestand staat in %PROGRAMDATA%\Jabasoft\ai.json en niet in de
/// persoonlijke map van een gebruiker: de instelling geldt voor de hele
/// machine. Ook niet naast de exe van de broker - die map wordt bij elke
/// schone build leeggehaald.
///
/// Lukt schrijven niet (geen rechten), dan blijft de waarde wel in het
/// geheugen staan tot de broker stopt; de aanroeper krijgt terug wat er nu
/// geldt. Een instelling niet kunnen wegschrijven mag de broker niet
/// omleggen.
/// </summary>
public sealed class AiSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly Lock _slot = new();
    private AiSettings _current;

    public AiSettingsStore()
    {
        _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Jabasoft",
            "ai.json");

        _current = Lees();
    }

    /// <summary>Wat er nu ingesteld staat.</summary>
    public AiSettings Current
    {
        get
        {
            lock (_slot)
            {
                return _current;
            }
        }
    }

    /// <summary>
    /// Slaat een nieuwe instelling op en geeft terug wat er daadwerkelijk
    /// is neergezet - dat kan afwijken van wat er binnenkwam, want lege
    /// adressen worden vervangen door de standaardpoort van die soort.
    /// </summary>
    public AiSettings Save(AiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var schoon = Normaliseer(settings);

        lock (_slot)
        {
            _current = schoon;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(schoon, Options));
            }
            catch (Exception)
            {
                // Zie de toelichting op deze klasse: niet kunnen bewaren mag
                // niets breken. De waarde geldt dan tot de broker stopt.
            }

            return schoon;
        }
    }

    /// <summary>Lege of rommelige adressen terug naar de standaardpoort, spaties eraf, de ondergrens binnen 0-1, het aantal contextronden binnen 0-5, en de wachttijd binnen 1-60 minuten.</summary>
    private static AiSettings Normaliseer(AiSettings settings) => settings with
    {
        LmStudio = Normaliseer(settings.LmStudio, AiSettings.DefaultLmStudioUrl),
        Ollama = Normaliseer(settings.Ollama, AiSettings.DefaultOllamaUrl),
        MinimumSemanticScore = Math.Clamp(settings.MinimumSemanticScore, 0, 1),
        MaxContextRondes = Math.Clamp(settings.MaxContextRondes, 0, 5),
        ChatTimeoutSeconden = Math.Clamp(settings.ChatTimeoutSeconden, 60, 3600),
    };

    /// <summary>
    /// Ook bestand tegen een ontbrekend blok: een instellingenbestand van
    /// voor de splitsing per serversoort heeft er geen, en dan is null hier
    /// het eerste wat binnenkomt.
    /// </summary>
    private static AiServerSettings Normaliseer(AiServerSettings? server, string standaardUrl)
    {
        if (server is null)
        {
            return new AiServerSettings(standaardUrl, string.Empty, string.Empty, string.Empty, string.Empty);
        }

        var url = server.Url?.Trim();

        return server with
        {
            Url = string.IsNullOrWhiteSpace(url) ? standaardUrl : url,
            ChatModel = server.ChatModel?.Trim() ?? string.Empty,
            EmbedModel = server.EmbedModel?.Trim() ?? string.Empty,
            CodeModel = server.CodeModel?.Trim() ?? string.Empty,
            ControleModel = server.ControleModel?.Trim() ?? string.Empty,
            BeeldModel = server.BeeldModel?.Trim() ?? string.Empty,
        };
    }

    private AiSettings Lees()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return AiSettings.Default;
            }

            var settings = JsonSerializer.Deserialize<AiSettings>(File.ReadAllText(_path), Options);
            return settings is null ? AiSettings.Default : Normaliseer(settings);
        }
        catch (Exception)
        {
            // Kapot of onleesbaar bestand: terug naar de standaard, en de
            // eerstvolgende keer opslaan zet het weer goed.
            return AiSettings.Default;
        }
    }
}
