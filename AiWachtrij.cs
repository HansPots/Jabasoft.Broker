using Jabasoft.Base.AiBroker;

namespace Jabasoft.Broker;

/// <summary>
/// Houdt bij welke vragen er aan de modellen gesteld zijn: welke er loopt,
/// welke er wachten en welke net klaar zijn. Het WACHTEN zelf doet
/// <see cref="RequestGate"/> (één vraag tegelijk per server, op volgorde van
/// binnenkomst); dit is de boekhouding eromheen, zodat elke applicatie in
/// haar footer kan laten zien wat er gebeurt - en een vraag kan annuleren.
///
/// Annuleren werkt via een eigen CancellationTokenSource per vraag, gekoppeld
/// aan die van het binnenkomende verzoek: een wachtende vraag valt dan uit de
/// rij voor hij aan de beurt is, een lopende wordt afgebroken.
/// </summary>
internal sealed class AiWachtrij
{
    /// <summary>Zo lang blijft een afgeronde vraag nog in de lijst staan, zodat je ziet dat hij klaar is.</summary>
    private static readonly TimeSpan ToonAfgerondNog = TimeSpan.FromSeconds(30);

    private readonly Lock _slot = new();
    private readonly List<Vraag> _vragen = [];
    private long _volgnummer;

    /// <summary>
    /// Meldt een vraag aan. Gebruik het teruggegeven <see cref="Vraag.Token"/>
    /// voor het wachten én het werk, en sluit af met <see cref="Afronden"/>.
    /// </summary>
    public Vraag Aanmelden(string applicatie, string? onderdeel, string model, string soort, CancellationToken verzoek)
    {
        lock (_slot)
        {
            RuimOp();
            var vraag = new Vraag(++_volgnummer, applicatie, onderdeel, model, soort, CancellationTokenSource.CreateLinkedTokenSource(verzoek));
            _vragen.Add(vraag);
            return vraag;
        }
    }

    /// <summary>De vraag is aan de beurt (RequestGate liet hem door).</summary>
    public void Begonnen(Vraag vraag)
    {
        lock (_slot)
        {
            vraag.Status = WachtrijStatus.Bezig;
            vraag.Gestart = DateTimeOffset.Now;
        }
    }

    /// <summary>Een stukje antwoord van een lopende vraag: het inleespercentage of een stukje tekst/denkwerk.</summary>
    public void Voortgang(Vraag vraag, ChatStreamChunk stukje)
    {
        lock (_slot)
        {
            if (stukje.PromptProgress is { } voortgang)
            {
                vraag.Inlezen = voortgang;
            }

            if (!string.IsNullOrEmpty(stukje.Text) || stukje.Thinking)
            {
                vraag.Tokens++;
            }
        }
    }

    /// <summary>Klaar - met of zonder fout. Een geannuleerde vraag blijft geannuleerd, ook als er daarna nog een fout volgt.</summary>
    public void Afronden(Vraag vraag, string? fout)
    {
        lock (_slot)
        {
            if (vraag.Status != WachtrijStatus.Geannuleerd)
            {
                vraag.Status = fout is null ? WachtrijStatus.Klaar : WachtrijStatus.Mislukt;
                vraag.Fout = fout;
            }

            vraag.Afgerond = DateTimeOffset.Now;
        }

        vraag.Annulering.Dispose();
    }

    /// <summary>Haalt een wachtende vraag uit de rij of breekt een lopende af. False als hij niet bestaat of al klaar is.</summary>
    public bool Annuleer(long id)
    {
        Vraag? vraag;
        lock (_slot)
        {
            vraag = _vragen.FirstOrDefault(v => v.Id == id && v.Status is WachtrijStatus.Wachtend or WachtrijStatus.Bezig);
            if (vraag is null)
            {
                return false;
            }

            vraag.Status = WachtrijStatus.Geannuleerd;
            vraag.Fout = "Geannuleerd.";
        }

        try
        {
            vraag.Annulering.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Net op dit moment klaar.
        }

        return true;
    }

    /// <summary>Is deze vraag met de Annuleren-knop gestopt (en niet doordat de aanvrager zelf ophing)?</summary>
    public static bool IsGeannuleerd(Vraag vraag) => vraag.Status == WachtrijStatus.Geannuleerd;

    /// <summary>De lijst voor het scherm: eerst wat loopt, dan wat wacht (met hun plek), dan wat net klaar is.</summary>
    public IReadOnlyList<WachtrijItem> Lijst()
    {
        lock (_slot)
        {
            RuimOp();

            var plek = 0;
            return _vragen
                .OrderBy(v => v.Status switch { WachtrijStatus.Bezig => 0, WachtrijStatus.Wachtend => 1, _ => 2 })
                .ThenBy(v => v.Status is WachtrijStatus.Bezig or WachtrijStatus.Wachtend ? v.Id : -v.Id)
                .Select(v => new WachtrijItem(
                    v.Id,
                    v.Applicatie,
                    v.Onderdeel,
                    v.Model,
                    v.Soort,
                    v.Status,
                    v.Status == WachtrijStatus.Wachtend ? ++plek : 0,
                    v.Inlezen,
                    v.Tokens,
                    v.Aangemeld,
                    v.Gestart,
                    v.Afgerond,
                    v.Fout))
                .ToList();
        }
    }

    private void RuimOp() =>
        _vragen.RemoveAll(v => v.Afgerond is { } klaar && DateTimeOffset.Now - klaar > ToonAfgerondNog);

    /// <summary>Eén vraag in de boekhouding. Alleen <see cref="AiWachtrij"/> verandert hem, onder zijn slot.</summary>
    internal sealed class Vraag(long id, string applicatie, string? onderdeel, string model, string soort, CancellationTokenSource annulering)
    {
        public long Id { get; } = id;
        public string Applicatie { get; } = applicatie;
        public string? Onderdeel { get; } = onderdeel;
        public string Model { get; } = model;
        public string Soort { get; } = soort;
        public CancellationTokenSource Annulering { get; } = annulering;
        public DateTimeOffset Aangemeld { get; } = DateTimeOffset.Now;

        public WachtrijStatus Status { get; set; } = WachtrijStatus.Wachtend;
        public DateTimeOffset? Gestart { get; set; }
        public DateTimeOffset? Afgerond { get; set; }
        public double? Inlezen { get; set; }
        public long Tokens { get; set; }
        public string? Fout { get; set; }

        /// <summary>Het token voor wachten en werk: gaat af als de aanvrager ophangt of als iemand op Annuleren klikt.</summary>
        public CancellationToken Token => Annulering.Token;
    }
}
