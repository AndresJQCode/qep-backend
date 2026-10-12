using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-11 §6.5 y §7: el reclamo escribe <c>kind</c> desde el borrador y, si trae medio, la fila de
/// <c>message_media</c> en la misma transacción, con <c>stored_at</c> (fuera de la cola de copia). En <c>Failed</c>
/// también (D-V1), con <c>meta_media_id = ''</c> si Meta nunca dio un id (D-V2); el reintento sobrescribe la misma
/// fila. Sin migración.</summary>
public sealed class OutboundAudioPersistenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly OutboundMedia Voice = new("audio/ogg", 1234, new string('a', 64));

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

    private static OutboundDraft AudioDraft(Seeded seeded, Guid clientId, DateTimeOffset now) =>
        new(Guid.CreateVersion7(), seeded.ConversationId, seeded.TenantId, seeded.ConnectionId, clientId, null, seeded.MemberId, now,
            Kind: MessageKind.Audio, Media: Voice);

    [Fact]
    public async Task ASentVoiceNoteWritesKindFourAndItsStoredMediaInTheSameTransaction()
    {
        await using var database = await StartDatabaseAsync();
        var seeded = await SeedAsync(database);
        using var _ = seeded.Factory;
        var at = DateTimeOffset.UtcNow;
        var draft = AudioDraft(seeded, Guid.CreateVersion7(), at);

        using (var scope = seeded.Factory.Services.CreateScope())
        {
            await using var claim = await scope.ServiceProvider.GetRequiredService<IOutboundMessages>().ClaimAsync(draft, Ct);
            await claim.CommitSentAsync("wamid.voice", at, "media.out", Ct);
        }

        Assert.Equal("4|true|wamid.voice", await ScalarAsync<string>(seeded.ConnectionString,
            "SELECT kind || '|' || (text IS NULL) || '|' || wamid FROM messaging.messages WHERE id = @id", ("id", draft.MessageId)));
        Assert.Equal($"audio/ogg|media.out|{new string('a', 64)}|1234|messaging/{seeded.TenantId}/{draft.MessageId}|true|0", await ScalarAsync<string>(seeded.ConnectionString,
            "SELECT mime_type || '|' || meta_media_id || '|' || sha256 || '|' || size_bytes || '|' || storage_key || '|' || (stored_at IS NOT NULL) || '|' || attempts FROM messaging.message_media WHERE message_id = @id",
            ("id", draft.MessageId)));
        // La foto de la conversación, como un audio entrante (InboundIngestion.Preview): kind 4 y sin preview.
        Assert.Equal("4|true", await ScalarAsync<string>(seeded.ConnectionString,
            "SELECT last_message_kind || '|' || (last_message_preview IS NULL) FROM messaging.conversations WHERE id = @id", ("id", seeded.ConversationId)));
        // Fuera de IX_message_media_pending: el MediaCopyWorker sólo toma stored_at IS NULL.
        Assert.Equal(0L, await CountAsync(seeded.ConnectionString, "SELECT count(*) FROM messaging.message_media WHERE stored_at IS NULL"));
    }

    [Fact]
    public async Task AFailedVoiceNoteKeepsItsMediaWithoutMetaIdAndTheRetryOverwritesTheSameRow()
    {
        await using var database = await StartDatabaseAsync();
        var seeded = await SeedAsync(database);
        using var _ = seeded.Factory;
        var at = DateTimeOffset.UtcNow;
        var clientId = Guid.CreateVersion7();
        var first = AudioDraft(seeded, clientId, at);

        using (var scope = seeded.Factory.Services.CreateScope())
        {
            await using var claim = await scope.ServiceProvider.GetRequiredService<IOutboundMessages>().ClaimAsync(first, Ct);
            await claim.CommitFailedAsync(MessageFailureReasons.Unconfirmed, null, at, null, Ct);
        }

        Assert.Equal($"|messaging/{seeded.TenantId}/{first.MessageId}|true", await ScalarAsync<string>(seeded.ConnectionString,
            "SELECT meta_media_id || '|' || storage_key || '|' || (stored_at IS NOT NULL) FROM messaging.message_media WHERE message_id = @id", ("id", first.MessageId)));

        ExistingOutbound? existing;
        using (var scope = seeded.Factory.Services.CreateScope())
        {
            existing = await scope.ServiceProvider.GetRequiredService<IOutboundMessages>().FindByClientIdAsync(seeded.ConversationId, clientId, Ct);
        }

        Assert.NotNull(existing);
        Assert.Equal((MessageStatus.Failed, MessageKind.Audio, "audio/ogg"), (existing.Status, existing.Kind, existing.MediaMimeType));

        var retry = AudioDraft(seeded, clientId, at.AddMinutes(1));
        using (var scope = seeded.Factory.Services.CreateScope())
        {
            await using var claim = await scope.ServiceProvider.GetRequiredService<IOutboundMessages>().ClaimAsync(retry, Ct);
            Assert.False(claim.Inserted);
            Assert.Equal(first.MessageId, claim.MessageId);
            Assert.Equal((MessageKind.Audio, "audio/ogg"), (claim.Existing!.Kind, claim.Existing.MediaMimeType));
            await claim.CommitSentAsync("wamid.retry", at.AddMinutes(1), "media.2", Ct);
        }

        // Una sola fila de medio, la del messageId original, con el id nuevo de Meta: no quedan huérfanos.
        Assert.Equal($"1|media.2|messaging/{seeded.TenantId}/{first.MessageId}", await ScalarAsync<string>(seeded.ConnectionString,
            "SELECT count(*) OVER () || '|' || meta_media_id || '|' || storage_key FROM messaging.message_media"));
    }

    [Fact]
    public async Task ATextMessageStaysKindOneWithoutMediaAndWithItsPreview()
    {
        await using var database = await StartDatabaseAsync();
        var seeded = await SeedAsync(database);
        using var _ = seeded.Factory;
        var at = DateTimeOffset.UtcNow;
        var draft = new OutboundDraft(Guid.CreateVersion7(), seeded.ConversationId, seeded.TenantId, seeded.ConnectionId, Guid.CreateVersion7(), "hola", seeded.MemberId, at);

        using (var scope = seeded.Factory.Services.CreateScope())
        {
            await using var claim = await scope.ServiceProvider.GetRequiredService<IOutboundMessages>().ClaimAsync(draft, Ct);
            await claim.CommitSentAsync("wamid.text", at, null, Ct);
        }

        Assert.Equal("1|0|1|hola", await ScalarAsync<string>(seeded.ConnectionString,
            """
            SELECT m.kind || '|' || (SELECT count(*) FROM messaging.message_media mm WHERE mm.message_id = m.id) || '|' || c.last_message_kind || '|' || c.last_message_preview
            FROM messaging.messages m JOIN messaging.conversations c ON c.id = m.conversation_id WHERE m.id = @id
            """, ("id", draft.MessageId)));
    }
}
