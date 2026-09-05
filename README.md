# Jabasoft.Broker

Eén centraal draaiend proces dat alle aanroepen naar lokale LLM's (Ollama/LM
Studio) voor de hele JabaSoft-familie doet, in plaats van dat elke app dat
zelf apart doet. Regelt:

- de daadwerkelijke HTTP-aanroepen naar het model (chat, embeddings, modellenlijst, verbindingstest);
- een wachtrij per `(provider, serverUrl)` zodat gelijktijdige aanvragen van meerdere apps niet tegelijk bij dezelfde lokale server aankomen;
- een korte cache voor de modellenlijst/bereikbaarheids-check, zodat niet elke app apart hoeft te pollen;
- het wegschrijven van tokenverbruik naar de gedeelde `JabasoftBase`-database (via `Shared.Telemetry`), zodat apps dat niet meer zelf hoeven te doen.

Elke app **behoudt zijn eigen instellingen** (provider, server-URL, welk
model) — die geeft de app gewoon per aanroep mee; de broker onthoudt zelf
geen instellingen.

## Starten

Draait niet altijd (geen Windows-service) — wordt automatisch gestart door
welke JabaSoft-app dan ook als eerste opstart (zie
`Jabasoft.Base/AiBroker/AiBrokerProcessLauncher.cs`), en blijft daarna
draaien totdat je 'm zelf stopt, ook als alle apps weer dicht zijn.

Handmatig starten voor development:
```
dotnet run --no-launch-profile --urls http://localhost:5310
```

## Hergebruik door een app

```xml
<ProjectReference Include="..\..\Jabasoft.Base\Jabasoft.Base.csproj" />
```
```csharp
builder.Services.AddHttpClient<IAiBrokerClient, AiBrokerClient>(c =>
    c.BaseAddress = new Uri(AiBrokerClient.DefaultBaseUrl));
```

Zie `Jabasoft.Base/AiBroker/IAiBrokerClient.cs` voor de contracten
(`ChatAsync`, `EmbedAsync`, `TestConnectionAsync`, `ListModelsAsync`).

Referenties naar sibling-repo's onder `C:\Repos`: `Jabasoft.Shared`
(`Shared.Telemetry`, voor het wegschrijven van tokenverbruik) en
`Jabasoft.Base` (de gedeelde client-contracten hierboven).
