using System.Text.Json;
using Modules.Notifications.Application;
using static Modules.Notifications.IntegrationTests.NotificationsDeliveryHarness;

namespace Modules.Notifications.IntegrationTests;

/// <summary>
/// Lo que cada uno de los seis workers hace hoy, fijado antes de pasarlos a OutboxDeliveryWorker
/// (spec 2026-09-13, A6): su evento, su plantilla, un solo correo por mensaje y los dos caminos que
/// terminan en Failed sin enviar. Corre con los workers hospedados de verdad, así que no depende de
/// ninguna costura interna y vale igual antes y después del refactor.
/// </summary>
public sealed class DeliveryWorkersCharacterizationTests
{
    public static TheoryData<string, string> Events => new()
    {
        { "tenancy.membership-invited.v1", InvitationEmailTemplate.TemplateRef },
        { "customers.export-ready.v1", CustomerExportEmailTemplate.TemplateRef },
        { "catalog.product-export-ready.v1", ProductExportEmailTemplate.TemplateRef },
        { "quotations.export-ready.v1", QuotationsExportReadyEmailTemplate.TemplateRef },
        { "quotations.export-failed.v1", QuotationsExportFailedEmailTemplate.TemplateRef },
        { "suppliers.export-ready.v1", SupplierExportEmailTemplate.TemplateRef },
    };

    [Theory]
    [MemberData(nameof(Events))]
    public async Task EachWorkerDeliversItsEventOnceToItsRecipient(string eventName, string templateRef)
    {
        await using var database = await StartDatabaseAsync();
        var channel = new RecordingEmailChannel();
        using var factory = new NotificationsApiFactory(
            database.GetConnectionString(), channel, runDeliveryWorkers: true);
        var (tenantId, ownerUserId, email) = await RegisterOwnerAsync(factory);

        await InsertOutboxAsync(database.GetConnectionString(), eventName, PayloadFor(eventName, tenantId, ownerUserId));

        Assert.Equal(
            new NotificationRow("Sent", email, null),
            await WaitForNotificationAsync(database.GetConnectionString(), ownerUserId, templateRef));
        // Idempotente: los ticks siguientes no vuelven a mandar el mismo mensaje.
        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        Assert.Single(await NotificationsForAsync(database.GetConnectionString(), ownerUserId, templateRef));
        Assert.Single(channel.Sent, message => message.ToAddress == email);
    }

    // D-R2: la fecha y el conteo del correo salen del formato del tenant. register-tenant lo deja en
    // yyyy-MM-dd y America/Bogota; el formato numérico se cambia aparte, a punto decimal.
    [Theory]
    [InlineData("customers.export-ready.v1", CustomerExportEmailTemplate.TemplateRef)]
    [InlineData("catalog.product-export-ready.v1", ProductExportEmailTemplate.TemplateRef)]
    [InlineData("quotations.export-ready.v1", QuotationsExportReadyEmailTemplate.TemplateRef)]
    [InlineData("suppliers.export-ready.v1", SupplierExportEmailTemplate.TemplateRef)]
    public async Task EachExportEmailUsesTheTenantRegionalFormat(string eventName, string templateRef)
    {
        await using var database = await StartDatabaseAsync();
        var channel = new RecordingEmailChannel();
        using var factory = new NotificationsApiFactory(
            database.GetConnectionString(), channel, runDeliveryWorkers: true);
        var (tenantId, ownerUserId, email) = await RegisterOwnerAsync(factory);
        await SetTenantNumberFormatAsync(database.GetConnectionString(), tenantId, "1,234.56");

        await InsertOutboxAsync(
            database.GetConnectionString(),
            eventName,
            PayloadFor(eventName, tenantId, ownerUserId, count: 1234, expiresAt: new DateTimeOffset(2030, 1, 15, 15, 30, 0, TimeSpan.Zero)));

        var row = await WaitForNotificationAsync(
            database.GetConnectionString(), ownerUserId, templateRef);
        Assert.Equal(new NotificationRow("Sent", email, null), row);
        var message = Assert.Single(channel.Sent, sent => sent.ToAddress == email);
        Assert.Contains("2030-01-15 10:30", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("1,234 ", message.TextBody, StringComparison.Ordinal);
    }

    // Un evento encolado antes del token de invitación no tiene link que armar.
    [Fact]
    public async Task AnInvitationWithoutTokenIsFailedWithoutSending()
    {
        await using var database = await StartDatabaseAsync();
        var channel = new RecordingEmailChannel();
        using var factory = new NotificationsApiFactory(
            database.GetConnectionString(), channel, runDeliveryWorkers: true);
        var (tenantId, ownerUserId, email) = await RegisterOwnerAsync(factory);

        await InsertOutboxAsync(
            database.GetConnectionString(),
            "tenancy.membership-invited.v1",
            JsonSerializer.Serialize(new { userId = ownerUserId, tenantId = new { value = tenantId } }));

        Assert.Equal(
            new NotificationRow("Failed", email, "invitation_token_unavailable"),
            await WaitForNotificationAsync(database.GetConnectionString(), ownerUserId, InvitationEmailTemplate.TemplateRef));
        Assert.DoesNotContain(channel.Sent, message => message.ToAddress == email);
    }

    [Fact]
    public async Task ARecipientWithoutEmailIsFailedWithoutSending()
    {
        await using var database = await StartDatabaseAsync();
        var channel = new RecordingEmailChannel();
        using var factory = new NotificationsApiFactory(
            database.GetConnectionString(), channel, runDeliveryWorkers: true);
        var unknownUserId = Guid.CreateVersion7();
        // Acá no hay ClaimAsync ni request HTTP previo (no se registra owner) que dispare el arranque
        // del host, y sin él las migraciones no corrieron: el INSERT crudo de abajo falla con "relation
        // does not exist". Mismo precedente que InboxClaimsTests.
        _ = factory.Services;

        await InsertOutboxAsync(
            database.GetConnectionString(),
            "customers.export-ready.v1",
            PayloadFor("customers.export-ready.v1", Guid.CreateVersion7(), unknownUserId));

        Assert.Equal(
            new NotificationRow("Failed", string.Empty, "recipient_email_unavailable"),
            await WaitForNotificationAsync(database.GetConnectionString(), unknownUserId, CustomerExportEmailTemplate.TemplateRef));
        Assert.Empty(channel.Sent);
    }

    // Los payloads tal como los escriben los productores: Tenancy anida el tenantId en "value".
    private static string PayloadFor(
        string eventName, Guid tenantId, Guid subjectId, int count = 3, DateTimeOffset? expiresAt = null) => eventName switch
    {
        "tenancy.membership-invited.v1" => JsonSerializer.Serialize(
            new { userId = subjectId, tenantId = new { value = tenantId }, token = "invitation-token" }),
        "customers.export-ready.v1" => JsonSerializer.Serialize(new
        {
            tenantId,
            subjectId,
            downloadUrl = "https://r2.test/exports/clientes.xlsx?X-Amz-Signature=abc",
            fileName = "clientes.xlsx",
            customerCount = count,
            expiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddHours(24),
        }),
        "catalog.product-export-ready.v1" => JsonSerializer.Serialize(new
        {
            tenantId,
            subjectId,
            downloadUrl = "https://r2.test/exports/productos.xlsx?X-Amz-Signature=abc",
            fileName = "productos.xlsx",
            productCount = count,
            expiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddHours(24),
        }),
        "quotations.export-ready.v1" => JsonSerializer.Serialize(new
        {
            tenantId,
            subjectId,
            kind = "Quotations",
            downloadUrl = "https://r2.test/exports/cotizaciones.xlsx?X-Amz-Signature=abc",
            fileName = "cotizaciones-2026-09-13-1530.xlsx",
            rowCount = count,
            expiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddHours(24),
        }),
        "quotations.export-failed.v1" => JsonSerializer.Serialize(new { tenantId, subjectId, kind = "Orders" }),
        "suppliers.export-ready.v1" => JsonSerializer.Serialize(new
        {
            tenantId,
            subjectId,
            downloadUrl = "https://r2.test/exports/proveedores.xlsx?X-Amz-Signature=abc",
            fileName = "proveedores.xlsx",
            supplierCount = count,
            expiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddHours(24),
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(eventName), eventName, "No payload for this event."),
    };
}
