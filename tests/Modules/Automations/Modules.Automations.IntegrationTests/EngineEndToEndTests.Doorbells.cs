using System.Diagnostics;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

public sealed partial class EngineEndToEndTests
{
    /// <summary>
    /// Spec 2026-11-13 (Valkey), D-7; plan P12: con los timbres prendidos el worker real del motor espera hasta su red de seguridad
    /// (15 s) y no su intervalo; el entrante encola en <c>automation_inbox</c>, el interceptor timbra <c>qep:wake:automation</c> al
    /// confirmar y el bot responde mucho antes. <c>InboxPollSeconds = 60</c> deja claro que no fue el intervalo de hoy.
    /// </summary>
    [Fact]
    public async Task AnInboundMessageWakesTheBotBeforeTheSafetyNet()
    {
        await using var valkey = await ValkeyTestServer.StartAsync(Ct);
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database, settings: new Dictionary<string, string>
        {
            ["Automations:Workers:InboxPollSeconds"] = "60",
            ["Valkey:Enabled"] = "true",
            ["Valkey:Areas:Doorbells"] = "true",
            ["ConnectionStrings:Valkey"] = ValkeyTestServer.ConnectionString(valkey),
        });

        // El worker ya hizo su primera vuelta al arrancar el host y quedó esperando el timbre.
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        var stopwatch = Stopwatch.StartNew();
        await InboundAsync(f, "w1", "Hola");

        while (f.Meta.Sends().Count < 2 && stopwatch.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(100, Ct);
        }

        Assert.True(f.Meta.Sends().Count >= 2, $"El bot no respondió en {stopwatch.Elapsed}.");
    }
}
