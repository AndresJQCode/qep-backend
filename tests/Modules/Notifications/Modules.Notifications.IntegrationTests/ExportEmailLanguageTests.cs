using System.Text.Json;
using Modules.Notifications.Application;
using Npgsql;
using static Modules.Notifications.IntegrationTests.NotificationsDeliveryHarness;

namespace Modules.Notifications.IntegrationTests;

/// <summary>
/// Spec 2026-11-22 (i18n): el correo de exportación se escribe en el idioma de quien la pidió en ese
/// tenant; si no eligió uno, en el del tenant. En España la cotización es «presupuesto».
/// </summary>
public sealed class ExportEmailLanguageTests
{
    [Theory]
    [InlineData(null, null, "Tu exportación de cotizaciones está lista")]
    [InlineData("es-ES", null, "Tu exportación de presupuestos está lista")]
    [InlineData("es-ES", "es-419", "Tu exportación de cotizaciones está lista")]
    [InlineData(null, "es-ES", "Tu exportación de presupuestos está lista")]
    public async Task TheReadyEmailSpeaksTheLanguageOfWhoAskedForIt(
        string? tenantLanguage, string? personLanguage, string expectedSubject)
    {
        await using var database = await StartDatabaseAsync();
        var channel = new RecordingEmailChannel();
        using var factory = new NotificationsApiFactory(
            database.GetConnectionString(), channel, runDeliveryWorkers: true);
        var (tenantId, ownerUserId, email) = await RegisterOwnerAsync(factory);
        if (tenantLanguage is not null)
        {
            await ExecuteAsync(
                database.GetConnectionString(),
                "UPDATE tenancy.tenants SET language = @language WHERE id = @tenantId",
                ("language", tenantLanguage), ("tenantId", tenantId));
        }

        if (personLanguage is not null)
        {
            await ExecuteAsync(
                database.GetConnectionString(),
                """
                INSERT INTO identity.user_preferences (user_id, tenant_id, color_scheme, mode, message_sound, updated_at, language)
                VALUES (@userId, @tenantId, 'botanical', 'Light', true, now(), @language)
                """,
                ("userId", ownerUserId), ("tenantId", tenantId), ("language", personLanguage));
        }

        await InsertOutboxAsync(
            database.GetConnectionString(),
            "quotations.export-ready.v1",
            JsonSerializer.Serialize(new
            {
                tenantId,
                subjectId = ownerUserId,
                kind = "Quotations",
                downloadUrl = "https://r2.test/exports/x.xlsx?X-Amz-Signature=abc",
                fileName = "export.xlsx",
                rowCount = 3,
                expiresAt = DateTimeOffset.UtcNow.AddHours(24),
            }));

        Assert.Equal(
            new NotificationRow("Sent", email, null),
            await WaitForNotificationAsync(
                database.GetConnectionString(), ownerUserId, QuotationsExportReadyEmailTemplate.TemplateRef));
        var message = Assert.Single(channel.Sent, sent => sent.ToAddress == email);
        Assert.Equal(expectedSubject, message.Subject);
    }

    [Fact]
    public async Task TheFailedEmailFollowsTheTenantLanguage()
    {
        await using var database = await StartDatabaseAsync();
        var channel = new RecordingEmailChannel();
        using var factory = new NotificationsApiFactory(
            database.GetConnectionString(), channel, runDeliveryWorkers: true);
        var (tenantId, ownerUserId, email) = await RegisterOwnerAsync(factory);
        await ExecuteAsync(
            database.GetConnectionString(),
            "UPDATE tenancy.tenants SET language = 'es-ES' WHERE id = @tenantId",
            ("tenantId", tenantId));

        await InsertOutboxAsync(
            database.GetConnectionString(),
            "quotations.export-failed.v1",
            JsonSerializer.Serialize(new { tenantId, subjectId = ownerUserId, kind = "Quotations" }));

        await WaitForNotificationAsync(
            database.GetConnectionString(), ownerUserId, QuotationsExportFailedEmailTemplate.TemplateRef);
        var message = Assert.Single(channel.Sent, sent => sent.ToAddress == email);
        Assert.Equal("No pudimos generar tu exportación de presupuestos", message.Subject);
    }

    private static async Task ExecuteAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }
}
