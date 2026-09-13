using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Notifications.Application;
using Modules.Notifications.Infrastructure.Messaging;
using static Modules.Notifications.IntegrationTests.NotificationsDeliveryHarness;

namespace Modules.Notifications.IntegrationTests;

/// <summary>
/// El reclamo, el lease y el aislamiento de los workers de correo (spec 2026-09-13, Sección 1). Se
/// ejercitan sobre el worker de "exportación fallida", el de payload más corto: la mecánica es de la
/// base común, así que vale igual para los cinco. Los workers hospedados no corren
/// (NotificationsApiFactory): cada prueba llama a DrainAsync o arranca el suyo.
/// </summary>
public sealed class OutboxDeliveryWorkerTests
{
    private const string EventName = "quotations.export-failed.v1";
    private const string Consumer = "notifications.quotations-export-failed-email";
    private const string TemplateRef = QuotationsExportFailedEmailTemplate.TemplateRef;

    [Fact]
    public async Task TwoConcurrentDrainsSendTheMessageOnce()
    {
        await using var database = await StartDatabaseAsync();
        var channel = new RecordingEmailChannel
        {
            // Abre la ventana entre leer los candidatos y guardar: sin reclamo, los dos mandan.
            OnSend = _ => Task.Delay(TimeSpan.FromMilliseconds(500)),
        };
        using var factory = new NotificationsApiFactory(database.GetConnectionString(), channel);
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        await InsertOutboxAsync(database.GetConnectionString(), EventName, FailedPayload(tenantId, ownerUserId));

        await Task.WhenAll(
            NewWorker(factory).DrainAsync(TestContext.Current.CancellationToken),
            NewWorker(factory).DrainAsync(TestContext.Current.CancellationToken));

        Assert.Single(channel.Sent);
        var notification = Assert.Single(
            await NotificationsForAsync(database.GetConnectionString(), ownerUserId, TemplateRef));
        Assert.Equal("Sent", notification.Status);
    }

    // A4: un timeout del proveedor es una falla de envío, no un apagado. Corre el loop de verdad
    // (StartAsync), porque lo que se rompía era el loop.
    [Fact]
    public async Task AProviderTimeoutFailsTheMessageAndTheLoopKeepsGoing()
    {
        await using var database = await StartDatabaseAsync();
        var channel = new RecordingEmailChannel
        {
            // Lo que tira HttpClient cuando vence su Timeout: una cancelación que no es el apagado.
            OnSend = call => call == 1
                ? Task.FromException(new TaskCanceledException(
                    "The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing.",
                    new TimeoutException()))
                : Task.CompletedTask,
        };
        using var factory = new NotificationsApiFactory(database.GetConnectionString(), channel);
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var (secondTenantId, secondUserId, _) = await RegisterOwnerAsync(factory);
        var timedOut = await InsertOutboxAsync(
            database.GetConnectionString(), EventName, FailedPayload(tenantId, ownerUserId));

        var worker = NewWorker(factory);
        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var failed = await WaitForNotificationAsync(database.GetConnectionString(), ownerUserId, TemplateRef);
            Assert.Equal("Failed", failed?.Status);
            Assert.StartsWith("The request was canceled", failed?.FailureReason, StringComparison.Ordinal);
            var inbox = await FindInboxAsync(database.GetConnectionString(), Consumer, timedOut);
            Assert.NotNull(inbox?.ProcessedAt);

            // Un mensaje nuevo después del timeout: el mismo worker lo manda en un tick siguiente.
            await InsertOutboxAsync(
                database.GetConnectionString(), EventName, FailedPayload(secondTenantId, secondUserId));
            var sent = await WaitForNotificationAsync(database.GetConnectionString(), secondUserId, TemplateRef);
            Assert.Equal("Sent", sent?.Status);
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    // Un deploy detiene el pod justo después de que el correo salió. El cierre (notificación y
    // processed_at) no se puede cortar por el apagado: si se corta, el mensaje queda sin terminar y la
    // otra réplica lo vuelve a mandar cuando vence el lease.
    [Fact]
    public async Task AShutdownRightAfterTheSendStillClosesTheMessage()
    {
        await using var database = await StartDatabaseAsync();
        using var stopping = new CancellationTokenSource();
        var channel = new RecordingEmailChannel
        {
            // El apagado llega con el envío en vuelo, y el envío igual termina: el canal no mira el
            // token, así que lo cuenta como enviado.
            OnSend = _ =>
            {
                stopping.Cancel();
                return Task.CompletedTask;
            },
        };
        using var factory = new NotificationsApiFactory(database.GetConnectionString(), channel);
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var messageId = await InsertOutboxAsync(
            database.GetConnectionString(), EventName, FailedPayload(tenantId, ownerUserId));

        // Que DrainAsync termine o suba la cancelación da igual: con el apagado, el loop sale de todas
        // formas. Lo que importa es cómo quedó el mensaje.
        _ = await Record.ExceptionAsync(() => NewWorker(factory).DrainAsync(stopping.Token));

        Assert.Single(channel.Sent);
        var inbox = await FindInboxAsync(database.GetConnectionString(), Consumer, messageId);
        Assert.NotNull(inbox?.ProcessedAt);
        var notification = Assert.Single(
            await NotificationsForAsync(database.GetConnectionString(), ownerUserId, TemplateRef));
        Assert.Equal("Sent", notification.Status);
    }

    // El reclamo que retoma el mensaje suma un intento, así que el sembrado más uno es lo que ve el
    // worker. El corte está en attempts > 3 (spec 2026-09-13, Sección 1): con 2 sembrado el reclamo
    // llega a 3, justo en el borde, y todavía envía.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AClaimWhoseLeaseExpiredIsRetakenAndCountsTheAttempt(int seededAttempts)
    {
        await using var database = await StartDatabaseAsync();
        var channel = new RecordingEmailChannel();
        using var factory = new NotificationsApiFactory(database.GetConnectionString(), channel);
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var messageId = await InsertOutboxAsync(
            database.GetConnectionString(), EventName, FailedPayload(tenantId, ownerUserId));
        // Un worker que lo reclamó y murió antes de guardar: sin processed_at y con el lease vencido.
        await PutInboxAsync(
            database.GetConnectionString(), Consumer, messageId,
            processedAt: null, claimedUntil: DateTimeOffset.UtcNow.AddMinutes(-1), attempts: seededAttempts);

        await NewWorker(factory).DrainAsync(TestContext.Current.CancellationToken);

        Assert.Single(channel.Sent);
        var inbox = await FindInboxAsync(database.GetConnectionString(), Consumer, messageId);
        Assert.Equal(seededAttempts + 1, inbox?.Attempts);
        Assert.NotNull(inbox?.ProcessedAt);
    }

    // Tres reclamos sin terminar, o más: el siguiente no envía, registra el fallo y cierra el mensaje.
    // Con 3 sembrado el reclamo llega a 4, el primer valor que se corta.
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task AMessageClaimedThreeTimesOrMoreWithoutFinishingIsFailedWithoutSending(int seededAttempts)
    {
        await using var database = await StartDatabaseAsync();
        var channel = new RecordingEmailChannel();
        using var factory = new NotificationsApiFactory(database.GetConnectionString(), channel);
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var messageId = await InsertOutboxAsync(
            database.GetConnectionString(), EventName, FailedPayload(tenantId, ownerUserId));
        await PutInboxAsync(
            database.GetConnectionString(), Consumer, messageId,
            processedAt: null, claimedUntil: DateTimeOffset.UtcNow.AddMinutes(-1), attempts: seededAttempts);

        await NewWorker(factory).DrainAsync(TestContext.Current.CancellationToken);

        Assert.Empty(channel.Sent);
        // Sin destinatario: lo que está roto puede ser justamente el payload, así que no se lee.
        var poisoned = Assert.Single(
            await NotificationsForAsync(database.GetConnectionString(), Guid.Empty, TemplateRef));
        Assert.Equal(("Failed", "delivery_attempts_exhausted"), (poisoned.Status, poisoned.FailureReason));
        var inbox = await FindInboxAsync(database.GetConnectionString(), Consumer, messageId);
        Assert.Equal(seededAttempts + 1, inbox?.Attempts);
        Assert.NotNull(inbox?.ProcessedAt);
    }

    [Fact]
    public async Task AnUnreadablePayloadDoesNotStopTheRestOfTheBatch()
    {
        await using var database = await StartDatabaseAsync();
        var channel = new RecordingEmailChannel();
        using var factory = new NotificationsApiFactory(database.GetConnectionString(), channel);
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        // El roto va primero en el lote: sin aislamiento, su excepción abortaba todo lo que venía detrás.
        var broken = await InsertOutboxAsync(
            database.GetConnectionString(), EventName, "{}", DateTimeOffset.UtcNow.AddMinutes(-1));
        await InsertOutboxAsync(database.GetConnectionString(), EventName, FailedPayload(tenantId, ownerUserId));

        await NewWorker(factory).DrainAsync(TestContext.Current.CancellationToken);

        Assert.Single(channel.Sent);
        Assert.Equal(
            "Sent",
            Assert.Single(await NotificationsForAsync(database.GetConnectionString(), ownerUserId, TemplateRef)).Status);
        // El roto queda reclamado y sin terminar: vencido el lease se reintenta, y al tercero se corta.
        var inbox = await FindInboxAsync(database.GetConnectionString(), Consumer, broken);
        Assert.Equal(1, inbox?.Attempts);
        Assert.Null(inbox?.ProcessedAt);
    }

    private static string FailedPayload(Guid tenantId, Guid subjectId) =>
        JsonSerializer.Serialize(new { tenantId, subjectId, kind = "Quotations" });

    // La misma clase que registra el DI, construida a mano: mismo constructor, sin el PeriodicTimer.
    private static QuotationsExportFailedDeliveryWorker NewWorker(NotificationsApiFactory factory) =>
        new(factory.Services.GetRequiredService<IServiceScopeFactory>(),
            factory.Services.GetRequiredService<ILogger<QuotationsExportFailedDeliveryWorker>>());
}
