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
}
