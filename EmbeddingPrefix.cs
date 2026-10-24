namespace Jabasoft.Broker;

/// <summary>
/// Sommige embeddingmodellen zijn getraind met een vast voorvoegsel dat zegt
/// WAT voor tekst er komt: een document dat in de index gaat, of een vraag
/// waarmee gezocht wordt. Zonder dat voorvoegsel werken ze nog wel, maar
/// liggen alle scores dicht bij elkaar (bij nomic: alles tussen 0,50 en
/// 0,65) en maakt de drempel geen onderscheid meer tussen raak en ruis.
///
/// Alleen voor modellen die het echt verwachten - een ander model zou het
/// voorvoegsel gewoon als inhoud meenemen. De bewaarde tekst in de index
/// blijft zonder voorvoegsel; dit wordt alleen vlak voor het embedden ervoor
/// gezet, zodat herindexeren met een ander model schoon begint.
/// </summary>
internal static class EmbeddingPrefix
{
    /// <summary>De tekst zoals hij geëmbed wordt als hij IN de index gaat.</summary>
    public static string Document(string model, string tekst) =>
        IsNomic(model) ? "search_document: " + tekst : tekst;

    /// <summary>De tekst zoals hij geëmbed wordt als je ermee ZOEKT.</summary>
    public static string Vraag(string model, string tekst) =>
        IsNomic(model) ? "search_query: " + tekst : tekst;

    /// <summary>De nomic-embed-text-modellen (v1, v1.5, v2-moe), onder welke naam LM Studio of Ollama ze ook aanbiedt.</summary>
    private static bool IsNomic(string model) =>
        model.Contains("nomic-embed", StringComparison.OrdinalIgnoreCase);
}
