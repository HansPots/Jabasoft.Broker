using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Jabasoft.BrokerTray;

/// <summary>
/// Het pictogram in het systeemvak. Groen = de broker draait, grijs = gestopt, oranje = bezig met starten of
/// stoppen. Het menu start en stopt de broker op dezelfde manier als de beheerscripts in ..\Beheer, zodat er maar
/// één weg is: starten loopt via de aanmeldtaak "Jabasoft Broker" (bouwen, dan starten, met opstart.log), en
/// stoppen doet wat Broker-stoppen.ps1 doet. De broker zelf blijft dus alles doen wat hij deed, ook het
/// tokenverbruik in de database bijhouden.
/// </summary>
internal sealed class BrokerTray : IDisposable
{
    private const string BasisUrl = "http://localhost:5310";
    private const string Taaknaam = "Jabasoft Broker";
    private const string BrokerProces = "Jabasoft.Broker";

    /// <summary>Starten = eerst bouwen; dat kan na een grote wijziging even duren.</summary>
    private static readonly TimeSpan StartTijd = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan StopTijd = TimeSpan.FromSeconds(15);

    private static readonly string OpstartLog = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jabasoft", "Broker", "opstart.log");

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly NotifyIcon _icoon;
    private readonly System.Windows.Forms.Timer _klok = new() { Interval = 3000 };

    private readonly ToolStripMenuItem _status = new() { Enabled = false };
    private readonly ToolStripMenuItem _starten = new("Starten");
    private readonly ToolStripMenuItem _stoppen = new("Stoppen");
    private readonly ToolStripMenuItem _herstarten = new("Opnieuw starten");
    private readonly ToolStripMenuItem _aiServer = new("AI-server (chat)");
    private readonly ToolStripMenuItem _lmStudio = new("LM Studio");
    private readonly ToolStripMenuItem _ollama = new("Ollama") { Enabled = false };

    private readonly Icon _groen = Maak(Color.FromArgb(46, 158, 68));
    private readonly Icon _grijs = Maak(Color.FromArgb(128, 128, 128));
    private readonly Icon _oranje = Maak(Color.FromArgb(224, 160, 0));

    private bool _draait;
    private Bezig _bezig = Bezig.Niets;
    private DateTime _bezigTot;
    private bool _aanHetVerversen;

    private enum Bezig { Niets, Starten, Stoppen }

    public BrokerTray()
    {
        _status.Font = new Font(_status.Font, FontStyle.Bold);
        _starten.Click += async (_, _) => await StartAsync();
        _stoppen.Click += async (_, _) => await StopAsync(herstarten: false);
        _herstarten.Click += async (_, _) => await StopAsync(herstarten: true);
        _lmStudio.Click += async (_, _) => await ZetServerAsync("LmStudio");
        _ollama.Click += async (_, _) => await ZetServerAsync("Ollama");
        _aiServer.DropDownItems.AddRange([_lmStudio, _ollama]);

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            _status,
            new ToolStripSeparator(),
            _starten,
            _stoppen,
            _herstarten,
            new ToolStripSeparator(),
            _aiServer,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Opstartlog openen", null, (_, _) => OpenLog()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Pictogram sluiten (broker blijft draaien)", null, (_, _) => Application.Exit()),
        ]);

        // Bij openen: welke server staat er nu ingesteld (kan in Jabasoft omgezet zijn).
        menu.Opening += async (_, _) => await LeesServerAsync();

        _icoon = new NotifyIcon { ContextMenuStrip = menu, Visible = true };

        // Ook met de linkermuisknop het menu openen, niet alleen met rechts.
        _icoon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.Invoke(_icoon, null);
            }
        };

        _klok.Tick += async (_, _) => await VerversAsync();
        _klok.Start();

        Toon();
        _ = VerversAsync();
    }

    // ---- Toestand -------------------------------------------------------------------------------------------

    private async Task VerversAsync()
    {
        if (_aanHetVerversen)
        {
            return;
        }

        _aanHetVerversen = true;
        try
        {
            _draait = await IsGezondAsync();

            if (_bezig == Bezig.Starten && _draait)
            {
                _bezig = Bezig.Niets;
                _icoon.ShowBalloonTip(3000, "Jabasoft Broker", "De broker draait.", ToolTipIcon.Info);
            }
            else if (_bezig == Bezig.Stoppen && !_draait && !Process.GetProcessesByName(BrokerProces).Any())
            {
                _bezig = Bezig.Niets;
            }
            else if (_bezig != Bezig.Niets && DateTime.Now > _bezigTot)
            {
                var wat = _bezig == Bezig.Starten ? "starten" : "stoppen";
                _bezig = Bezig.Niets;
                _icoon.ShowBalloonTip(5000, "Jabasoft Broker",
                    $"Het {wat} van de broker lukte niet op tijd. Kijk in het opstartlog.", ToolTipIcon.Warning);
            }
        }
        finally
        {
            _aanHetVerversen = false;
        }

        Toon();
    }

    private void Toon()
    {
        var (tekst, icoon) = _bezig switch
        {
            Bezig.Starten => ("Broker: bezig met starten…", _oranje),
            Bezig.Stoppen => ("Broker: bezig met stoppen…", _oranje),
            _ when _draait => ("Broker: draait", _groen),
            _ => ("Broker: gestopt", _grijs),
        };

        _status.Text = tekst;
        _icoon.Text = "Jabasoft " + tekst;
        _icoon.Icon = icoon;

        var vrij = _bezig == Bezig.Niets;
        _starten.Enabled = vrij && !_draait;
        _stoppen.Enabled = vrij && (_draait || Process.GetProcessesByName(BrokerProces).Any());
        _herstarten.Enabled = vrij && _draait;
        _aiServer.Enabled = vrij && _draait;
    }

    // ---- AI-server --------------------------------------------------------------------------------------------

    /// <summary>Vinkt de chatserver aan die nu bij de broker ingesteld staat.</summary>
    private async Task LeesServerAsync()
    {
        var instelling = await HaalInstellingAsync();
        var provider = instelling?["provider"]?.GetValue<string>();
        _lmStudio.Checked = provider == "LmStudio";
        _ollama.Checked = provider == "Ollama";

        // Ollama alleen kiesbaar als het draait: anders werkt na de wissel geen enkele chat meer (apps, Goose,
        // Hermes). Staat hij er al op, dan blijft hij aangevinkt zichtbaar.
        var ollamaUrl = instelling?["ollama"]?["url"]?.GetValue<string>() ?? "http://localhost:11434";
        var bereikbaar = await IsBereikbaarAsync(ollamaUrl.TrimEnd('/') + "/api/version");
        _ollama.Enabled = bereikbaar;
        _ollama.Text = bereikbaar ? "Ollama" : "Ollama (niet bereikbaar)";
    }

    private async Task<bool> IsBereikbaarAsync(string url)
    {
        try
        {
            using var antwoord = await _http.GetAsync(url);
            return antwoord.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Zet de chatserver om - voor alle apps, en voor Goose en Hermes, die via de broker lopen. Alleen het veld
    /// "provider" verandert; de rest van de instelling (ook de zoekserver, en dus de zoekindex) blijft zoals hij was.
    /// Daarom zonder bevestiging.
    /// </summary>
    private async Task ZetServerAsync(string provider)
    {
        var instelling = await HaalInstellingAsync();
        if (instelling is null)
        {
            _icoon.ShowBalloonTip(5000, "Jabasoft Broker", "De instelling is niet op te halen bij de broker.", ToolTipIcon.Error);
            return;
        }

        if (instelling["provider"]?.GetValue<string>() == provider)
        {
            return;
        }

        instelling["provider"] = provider;

        try
        {
            using var inhoud = new StringContent(instelling.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            using var antwoord = await _http.PutAsync($"{BasisUrl}/api/settings", inhoud);
            antwoord.EnsureSuccessStatusCode();

            var naam = provider == "Ollama" ? "Ollama" : "LM Studio";
            _icoon.ShowBalloonTip(3000, "Jabasoft Broker", $"De chat loopt nu via {naam}.", ToolTipIcon.Info);
        }
        catch (Exception fout)
        {
            _icoon.ShowBalloonTip(5000, "Jabasoft Broker", $"Omzetten lukte niet: {fout.Message}", ToolTipIcon.Error);
        }

        await LeesServerAsync();
    }

    private async Task<System.Text.Json.Nodes.JsonObject?> HaalInstellingAsync()
    {
        try
        {
            var tekst = await _http.GetStringAsync($"{BasisUrl}/api/settings");
            return System.Text.Json.Nodes.JsonNode.Parse(tekst) as System.Text.Json.Nodes.JsonObject;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void ZetBezig(Bezig bezig, TimeSpan duur)
    {
        _bezig = bezig;
        _bezigTot = DateTime.Now + duur;
        Toon();
    }

    // ---- Acties ---------------------------------------------------------------------------------------------

    private async Task StartAsync()
    {
        ZetBezig(Bezig.Starten, StartTijd);

        // Via de aanmeldtaak: dezelfde weg als bij aanmelden, dus bouwen + opstart.log, en de broker hangt aan
        // Taakplanner in plaats van aan dit pictogram. Is de taak er niet (Broker-installeren.ps1 nooit
        // gedraaid), dan het startscript rechtstreeks.
        var gelukt = await Task.Run(() => Voer("schtasks.exe", "/run", "/tn", Taaknaam) == 0);
        if (!gelukt)
        {
            var script = Path.Combine(BrokerMap(), "Beheer", "Broker-starten.ps1");
            if (!File.Exists(script) ||
                StartLos("powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script) is null)
            {
                _bezig = Bezig.Niets;
                _icoon.ShowBalloonTip(5000, "Jabasoft Broker", "Kon de broker niet starten.", ToolTipIcon.Error);
                Toon();
            }
        }
    }

    private async Task StopAsync(bool herstarten)
    {
        // Loopt er nog een vraag? Dan eerst vragen: stoppen breekt die af.
        var lopend = await LopendeVragenAsync();
        if (lopend.Count > 0)
        {
            var wie = string.Join(", ", lopend.Distinct());
            var vraag = lopend.Count == 1 ? "Er loopt nog 1 vraag" : $"Er lopen nog {lopend.Count} vragen";
            var antwoord = MessageBox.Show(
                $"{vraag} ({wie}). Die wordt afgebroken.\n\nToch {(herstarten ? "opnieuw starten" : "stoppen")}?",
                "Jabasoft Broker", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

            if (antwoord != DialogResult.Yes)
            {
                return;
            }
        }

        ZetBezig(Bezig.Stoppen, StopTijd);
        await Task.Run(StopBroker);
        await VerversAsync();

        if (herstarten)
        {
            // Even wachten tot de poort echt vrij is, anders loopt de nieuwe broker daartegenaan.
            var tot = DateTime.Now + StopTijd;
            while (DateTime.Now < tot && await IsGezondAsync())
            {
                await Task.Delay(500);
            }

            _bezig = Bezig.Niets;
            await StartAsync();
        }
    }

    /// <summary>Wat Broker-stoppen.ps1 doet: elk brokerproces stoppen, hoe het ook gestart is.</summary>
    private static void StopBroker()
    {
        foreach (var proces in Process.GetProcessesByName(BrokerProces))
        {
            using (proces)
            {
                try
                {
                    proces.Kill();
                    proces.WaitForExit(5000);
                }
                catch (Exception)
                {
                    // Al weg, of geen rechten: niets aan te doen.
                }
            }
        }
    }

    private static void OpenLog()
    {
        if (File.Exists(OpstartLog))
        {
            Process.Start(new ProcessStartInfo(OpstartLog) { UseShellExecute = true })?.Dispose();
        }
        else
        {
            MessageBox.Show($"Er is nog geen opstartlog:\n{OpstartLog}", "Jabasoft Broker",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    // ---- Broker bevragen ------------------------------------------------------------------------------------

    private async Task<bool> IsGezondAsync()
    {
        try
        {
            using var antwoord = await _http.GetAsync($"{BasisUrl}/health");
            return antwoord.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>De applicaties van de vragen die nu bezig zijn of wachten (één naam per vraag).</summary>
    private async Task<List<string>> LopendeVragenAsync()
    {
        var resultaat = new List<string>();
        try
        {
            await using var stroom = await _http.GetStreamAsync($"{BasisUrl}/api/queue");
            using var json = await JsonDocument.ParseAsync(stroom);

            foreach (var item in json.RootElement.EnumerateArray())
            {
                var status = item.TryGetProperty("status", out var s) ? s.GetString() : null;
                if (status is "Bezig" or "Wachtend")
                {
                    resultaat.Add(item.TryGetProperty("applicatie", out var a) ? a.GetString() ?? "?" : "?");
                }
            }
        }
        catch (Exception)
        {
            // Broker niet bereikbaar of ander antwoord: dan valt er ook niets af te breken.
        }

        return resultaat;
    }

    // ---- Hulpjes --------------------------------------------------------------------------------------------

    /// <summary>De map van het Jabasoft.Broker-project, bij het bouwen vastgelegd (zie het .csproj).</summary>
    private static string BrokerMap() =>
        typeof(BrokerTray).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BrokerMap")?.Value
        ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static int Voer(string bestand, params string[] argumenten)
    {
        try
        {
            using var proces = StartLos(bestand, argumenten);
            if (proces is null)
            {
                return -1;
            }

            proces.WaitForExit(10000);
            return proces.HasExited ? proces.ExitCode : -1;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static Process? StartLos(string bestand, params string[] argumenten)
    {
        var info = new ProcessStartInfo(bestand) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in argumenten)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            return Process.Start(info);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Het JS-pictogram van Jabasoft, met rechtsonder een bolletje in de kleur van de toestand.</summary>
    private static Icon Maak(Color kleur)
    {
        using var beeld = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(beeld))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            using (var basis = JabasoftBeeld())
            {
                g.DrawImage(basis, 0, 0, 32, 32);
            }

            // Zwarte rand eromheen, zodat het bolletje los staat van de letters.
            using var rand = new SolidBrush(Color.Black);
            g.FillEllipse(rand, 17, 17, 15, 15);
            using var vulling = new SolidBrush(kleur);
            g.FillEllipse(vulling, 19, 19, 11, 11);
        }

        var handvat = beeld.GetHicon();
        using var tijdelijk = Icon.FromHandle(handvat);
        var icoon = (Icon)tijdelijk.Clone();
        DestroyIcon(handvat);
        return icoon;
    }

    /// <summary>
    /// Het grootste beeld uit Jabasoft.ico (ingebakken in het exe-bestand). De beelden in dat bestand zijn PNG's;
    /// die lezen we rechtstreeks, zodat het niet afhangt van hoe goed Icon.ToBitmap met PNG-pictogrammen omgaat.
    /// </summary>
    private static Bitmap JabasoftBeeld()
    {
        using var stroom = typeof(BrokerTray).Assembly.GetManifestResourceStream("Jabasoft.ico")
            ?? throw new InvalidOperationException("Jabasoft.ico ontbreekt in het exe-bestand.");
        using var geheugen = new MemoryStream();
        stroom.CopyTo(geheugen);
        var bytes = geheugen.ToArray();

        // ICO-kop: aantal beelden op 4, dan per beeld 16 bytes (breedte, hoogte, ..., grootte op 8, plek op 12).
        var aantal = BitConverter.ToUInt16(bytes, 4);
        var (beste, grootte, plek) = (-1, 0, 0);
        for (var i = 0; i < aantal; i++)
        {
            var kop = 6 + (16 * i);
            var breedte = bytes[kop] == 0 ? 256 : bytes[kop];
            if (breedte >= 32 && (beste < 0 || breedte < beste))
            {
                // Het kleinste beeld van minstens 32 px: het scherpst na verkleinen.
                (beste, grootte, plek) = (breedte, (int)BitConverter.ToUInt32(bytes, kop + 8), (int)BitConverter.ToUInt32(bytes, kop + 12));
            }
        }

        using var beeld = new MemoryStream(bytes, plek, grootte);
        return new Bitmap(beeld);
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handvat);

    public void Dispose()
    {
        _klok.Dispose();
        _icoon.Visible = false;
        _icoon.Dispose();
        _http.Dispose();
        _groen.Dispose();
        _grijs.Dispose();
        _oranje.Dispose();
    }
}
