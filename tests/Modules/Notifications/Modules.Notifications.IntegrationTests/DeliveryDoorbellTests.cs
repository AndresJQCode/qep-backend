using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Notifications.Application;
using Modules.Notifications.Infrastructure.Persistence;
using static Modules.Notifications.IntegrationTests.NotificationsDeliveryHarness;

namespace Modules.Notifications.IntegrationTests;

/// <summary>
/// Spec 2026-11-13 (Valkey), D-7; plan P12: con los timbres prendidos el bucle de correo real espera hasta su red de seguridad
/// (30 s), no sus 15 s de hoy, y una fila nueva del outbox escrita por un <c>DbContext</c> lo despierta antes.
/// </summary>
public sealed class DeliveryDoorbellTests
{
    private const string EventName = "quotations.export-failed.v1";

    [Fact]
    public async Task AnOutboxRowWakesTheEmailConsumersBeforeTheSafetyNet()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var valkey = await ValkeyTestServer.StartAsync(ct);
        await using var database = await StartDatabaseAsync();
        var channel = new RecordingEmailChannel();
        using var factory = new NotificationsApiFactory(
            database.GetConnectionString(),
            channel,
            runDeliveryWorkers: true,
            settings: new Dictionary<string, string>
            {
                ["Valkey:Enabled"] = "true",
                ["Valkey:Areas:Doorbells"] = "true",
                ["ConnectionStrings:Valkey"] = ValkeyTestServer.ConnectionString(valkey),
            });
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);

        // Lo que dejó el registro ya timbró y se drenó; el bucle queda esperando el siguiente aviso.
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        var stopwatch = Stopwatch.StartNew();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            var payload = JsonSerializer.Serialize(new { tenantId, subjectId = ownerUserId, kind = "Quotations" });
            await context.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO platform.outbox_messages (id, event_name, payload, correlation_id, occurred_at, attempts)
                VALUES ({Guid.CreateVersion7()}, {EventName}, CAST({payload} AS jsonb), {Guid.NewGuid().ToString()}, {DateTimeOffset.UtcNow}, 0)
                """,
                ct);
        }

        IReadOnlyList<NotificationRow> rows = [];
        while (rows.Count == 0 && stopwatch.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(100, ct);
            rows = await NotificationsForAsync(
                database.GetConnectionString(), ownerUserId, QuotationsExportFailedEmailTemplate.TemplateRef);
        }

        Assert.True(rows.Count > 0, $"El correo no salió en {stopwatch.Elapsed}.");
    }
}
