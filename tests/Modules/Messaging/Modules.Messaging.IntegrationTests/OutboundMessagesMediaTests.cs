using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Messaging-media-reply R5 y R16: una imagen o un documento del asesor guardan su <c>caption</c> en
/// <c>messages</c> y su <c>file_name</c> en <c>message_media</c> (en el INSERT y en el <c>ON CONFLICT</c> del reintento), la
/// idempotencia los devuelve, y la foto de la conversación usa el caption como vista previa. Sin migración.</summary>
public sealed class OutboundMessagesMediaTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seeded(QepApiFactory Factory, string ConnectionString, Guid TenantId, Guid ConnectionId, Guid ConversationId, Guid MemberId);

    private static async Task<Seeded> SeedAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var conversationId = await SeedConversationAsync(factory, tenant.TenantId, connectionId, "573001234567");
        return new Seeded(factory, connectionString, tenant.TenantId, connectionId, conversationId, await OwnerMembershipIdAsync(connectionString, tenant));
    }

    private static OutboundDraft Draft(Seeded seeded, Guid clientId, DateTimeOffset now, MessageKind kind, OutboundMedia media, string? caption) =>
        new(Guid.CreateVersion7(), seeded.ConversationId, seeded.TenantId, seeded.ConnectionId, clientId, null, seeded.MemberId, now,
            Kind: kind, Media: media, Caption: caption);

    [Fact]
    public async Task ASentDocumentKeepsItsCaptionFileNameAndNullHashAndTheCaptionIsThePreview()
    {
        await using var database = await StartDatabaseAsync();
        var seeded = await SeedAsync(database);
        using var _ = seeded.Factory;
        var at = DateTimeOffset.UtcNow;
        var draft = Draft(seeded, Guid.CreateVersion7(), at, MessageKind.Document, new OutboundMedia("application/pdf", 5000, null, "Cotización mayo.pdf"), "Aquí va la cotización");

        using (var scope = seeded.Factory.Services.CreateScope())
        {
            await using var claim = await scope.ServiceProvider.GetRequiredService<IOutboundMessages>().ClaimAsync(draft, Ct);
            await claim.CommitSentAsync("wamid.doc", at, "media.doc", Ct);
        }

        Assert.Equal("5|true|Aquí va la cotización", await ScalarAsync<string>(seeded.ConnectionString,
            "SELECT kind || '|' || (text IS NULL) || '|' || caption FROM messaging.messages WHERE id = @id", ("id", draft.MessageId)));
        Assert.Equal("Cotización mayo.pdf|true|5000|media.doc", await ScalarAsync<string>(seeded.ConnectionString,
            "SELECT file_name || '|' || (sha256 IS NULL) || '|' || size_bytes || '|' || meta_media_id FROM messaging.message_media WHERE message_id = @id", ("id", draft.MessageId)));
        // Vista previa = Text ?? Caption: el documento no tiene texto.
        Assert.Equal("5|Aquí va la cotización", await ScalarAsync<string>(seeded.ConnectionString,
            "SELECT last_message_kind || '|' || last_message_preview FROM messaging.conversations WHERE id = @id", ("id", seeded.ConversationId)));
    }

    [Fact]
    public async Task AnImageWithoutCaptionLeavesNullCaptionAndNullPreview()
    {
        await using var database = await StartDatabaseAsync();
        var seeded = await SeedAsync(database);
        using var _ = seeded.Factory;
        var at = DateTimeOffset.UtcNow;
        var draft = Draft(seeded, Guid.CreateVersion7(), at, MessageKind.Image, new OutboundMedia("image/jpeg", 900, new string('b', 64), "foto.jpg"), null);

        using (var scope = seeded.Factory.Services.CreateScope())
        {
            await using var claim = await scope.ServiceProvider.GetRequiredService<IOutboundMessages>().ClaimAsync(draft, Ct);
            await claim.CommitSentAsync("wamid.img", at, "media.img", Ct);
        }

        Assert.Equal("2|true|foto.jpg", await ScalarAsync<string>(seeded.ConnectionString,
            "SELECT m.kind || '|' || (m.caption IS NULL) || '|' || mm.file_name FROM messaging.messages m JOIN messaging.message_media mm ON mm.message_id = m.id WHERE m.id = @id",
            ("id", draft.MessageId)));
        Assert.Equal("2|true", await ScalarAsync<string>(seeded.ConnectionString,
            "SELECT last_message_kind || '|' || (last_message_preview IS NULL) FROM messaging.conversations WHERE id = @id", ("id", seeded.ConversationId)));
    }

    [Fact]
    public async Task AFailedDocumentIsFoundByClientIdWithItsFileNameAndCaptionAndTheRetryOverwritesBoth()
    {
        await using var database = await StartDatabaseAsync();
        var seeded = await SeedAsync(database);
        using var _ = seeded.Factory;
        var at = DateTimeOffset.UtcNow;
        var clientId = Guid.CreateVersion7();
        var first = Draft(seeded, clientId, at, MessageKind.Document, new OutboundMedia("application/pdf", 5000, null, "viejo.pdf"), "Primero");

        using (var scope = seeded.Factory.Services.CreateScope())
        {
            await using var claim = await scope.ServiceProvider.GetRequiredService<IOutboundMessages>().ClaimAsync(first, Ct);
            await claim.CommitFailedAsync(MessageFailureReasons.Unconfirmed, null, at, null, Ct);
        }

        ExistingOutbound? existing;
        using (var scope = seeded.Factory.Services.CreateScope())
        {
            existing = await scope.ServiceProvider.GetRequiredService<IOutboundMessages>().FindByClientIdAsync(seeded.ConversationId, clientId, Ct);
        }

        Assert.NotNull(existing);
        Assert.Equal((MessageStatus.Failed, MessageKind.Document, "application/pdf", "viejo.pdf", "Primero"),
            (existing.Status, existing.Kind, existing.MediaMimeType, existing.MediaFileName, existing.Caption));

        var retry = Draft(seeded, clientId, at.AddMinutes(1), MessageKind.Document, new OutboundMedia("application/pdf", 6000, null, "nuevo.pdf"), "Segundo");
        using (var scope = seeded.Factory.Services.CreateScope())
        {
            await using var claim = await scope.ServiceProvider.GetRequiredService<IOutboundMessages>().ClaimAsync(retry, Ct);
            Assert.False(claim.Inserted);
            Assert.Equal(("viejo.pdf", "Primero"), (claim.Existing!.MediaFileName, claim.Existing.Caption));
            await claim.CommitSentAsync("wamid.retry", at.AddMinutes(1), "media.2", Ct);
        }

        // Una sola fila de medio con el nombre y el caption nuevos: el ON CONFLICT actualiza file_name y la fila de messages, caption.
        Assert.Equal("1|nuevo.pdf|6000|Segundo", await ScalarAsync<string>(seeded.ConnectionString,
            "SELECT count(*) OVER () || '|' || mm.file_name || '|' || mm.size_bytes || '|' || m.caption FROM messaging.message_media mm JOIN messaging.messages m ON m.id = mm.message_id"));
    }
}
