using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Jabasoft.Base.Logging;
using Jabasoft.Broker;

namespace Jabasoft.Broker;

public static class DashboardEndpoints
{
    public static void MapDashboard(this IEndpointRouteBuilder app, AiSettingsStore store, ActivityLog log)
    {
        app.MapGet("/dashboard", (AiSettingsStore store) => 
        {
            var data = new {
                Status = "Online",
                Settings = store.Current,
                QueueCount = 0
            };
            return Results.Ok(data);
        });

        app.MapGet("/dashboard-ui", (AiSettingsStore store) => 
        {
            var count = 0; // We fixen de wachtrij injectie in de volgende stap als nodig
            var provider = store.Current.Provider;
            var model = store.Current.Active.ChatModel;
            var url = store.Current.ActiveServerUrl;

            return Results.Content($@"<html>
            <head>
                <title>Jabasoft Broker Dashboard</title>
                <style>
                    body {{ background: #0f172a; color: white; font-family: sans-serif; padding: 2rem; }}
                    .card {{ background: #1e293b; padding: 1.5rem; border-radius: 10px; border: 1px solid #334155; margin-bottom: 1rem; }}
                    h1 {{ color: #22d3ee; }}
                    .status {{ color: #34d399; font-weight: bold; }}
                </style>
            </head>
            <body>
                <h1>Jabasoft Broker Dashboard</h1>
                <div class='card'>
                    <h2>Status: <span class='status'>Online</span></h2>
                    <p>Provider: {provider}</p>
                    <p>Active Model: {model}</p>
                    <p>Server URL: {url}</p>
                </div>
                <div class='card'>
                    <h2>Wachtrij</h2>
                    <p>Aantal items in wachtrij: {count}</p>
                </div>
            </body>
            </html>", "text/html");
        });
    }
}
