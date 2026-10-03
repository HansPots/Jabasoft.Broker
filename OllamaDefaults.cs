using Jabasoft.Base.AiBroker;

namespace Jabasoft.Broker;

/// <summary>
/// Gedeeld tussen ProviderClient (niet-streamend) en ProviderStream
/// (streamend) - beide sturen Ollama's /api/chat aan en liepen tegen
/// hetzelfde probleem aan.
/// </summary>
internal static class OllamaDefaults
{
    /// <summary>
    /// Het contextvenster dat we bij elke Ollama-aanroep opvragen, in
    /// plaats van zijn eigen standaard (4096) te laten gelden. Bij een
    /// afbeelding (die zelf al veel tokens kost) plus een redenerend model
    /// (Qwen3-VL, dat eerst hardop denkt in message.thinking vóór het
    /// echte antwoord) liep dat venster potdicht: de aanroep gaf 200 OK en
    /// "4096 tokens" terug, maar het hele budget was op aan denken - er
    /// kwam geen zichtbare tekst meer uit (zie ActivityLog: "Chat
    /// (stromend) met qwen3-vl:8b: 4096 tokens", 200 OK, leeg antwoord in
    /// de app).
    ///
    /// Empirisch bepaald (2026-09-28, RTX 4070 Super 12GB, qwen3-vl:8b
    /// Q4_K_M): 24576 laadt nog volledig op de GPU (`ollama ps` toont
    /// "100% GPU", 8.8GB); 28672 en 30720 ook nog 100% GPU (9.4/9.7GB);
    /// bij 32768 viel het al deels terug op de CPU (91%/9% GPU/CPU, 10GB).
    /// 24576 laat met opzet ruimte over (~3GB) voor een groter model of
    /// gelijktijdig ander GPU-gebruik (browser, desktop) - geen harde grens
    /// vanuit het model zelf, dat ondersteunt tot 262144.
    ///
    /// Geen dynamische berekening per model/GPU: dat zou de VRAM en de
    /// GGUF-metadata van het geladen model moeten opvragen en is voor deze
    /// ene-machine-instelling (zie AiSettingsStore) niet in verhouding tot
    /// gewoon een ruime vaste waarde die op deze machine geverifieerd is.
    /// </summary>
    public const int ContextLength = 24576;

    /// <summary>
    /// De stappen waarin de context groeit. Een model dat met een groot venster draait houdt per 1000 tokens
    /// ongeveer 160 MB videogeheugen vast (Qwen3-14B), dus bij 24576 is dat al ruim 3,5 GB voor een venster dat
    /// meestal grotendeels leeg blijft. We beginnen daarom klein en vergroten alleen als het nodig blijkt.
    /// </summary>
    public static readonly int[] Stappen = [2048, 4096, 8192, 16384, ContextLength];

    /// <summary>
    /// Modellen die eerst hardop nadenken (en dat doen in hetzelfde budget als het antwoord): DeepSeek-R1,
    /// Qwen3, GLM en soortgenoten. Die hebben een veel groter venster nodig dan hun vraag en antwoord doen vermoeden.
    /// </summary>
    private static readonly string[] DenkendeModellen = ["r1", "qwen3", "glm-4", "glm4", "think", "gpt-oss", "magistral"];

    public static bool Denkt(string model) =>
        DenkendeModellen.Any(naam => model.Contains(naam, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// De kleinste stap waarin de vraag past plus ruimte voor het antwoord. Een schatting: drie tekens per token is
    /// aan de voorzichtige kant, een afbeelding rekenen we op 2000 tokens. Een model dat denkt krijgt minstens
    /// 8192 en een flinke marge, omdat zijn nadenken evenveel ruimte inneemt als het antwoord zelf.
    /// </summary>
    public static int BeginContext(IReadOnlyList<ChatMessage> messages, string model)
    {
        var tekens = messages.Sum(bericht => bericht.Content?.Length ?? 0);
        var afbeeldingen = messages.Sum(bericht => bericht.Images?.Count ?? 0);
        var vraag = (tekens / 3) + (messages.Count * 20) + (afbeeldingen * 2000);

        var denkt = Denkt(model);
        var nodig = Math.Max(vraag + (denkt ? 5120 : 1536), denkt ? 8192 : 2048);

        return Stappen.FirstOrDefault(stap => stap >= nodig, ContextLength);
    }

    /// <summary>Deze stap en alle grotere: de volgorde waarin we het opnieuw proberen.</summary>
    public static IReadOnlyList<int> StappenVanaf(int begin) => [.. Stappen.Where(stap => stap >= begin)];
}
