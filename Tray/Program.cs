namespace Jabasoft.BrokerTray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Eén pictogram per gebruiker: een tweede start (aanmelden + dubbelklik) doet niets.
        using var eenmalig = new Mutex(true, @"Local\Jabasoft.BrokerTray", out var eerste);
        if (!eerste)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        using var tray = new BrokerTray();
        Application.Run();
    }
}
