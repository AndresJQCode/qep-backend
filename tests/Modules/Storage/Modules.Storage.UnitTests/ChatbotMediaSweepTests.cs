using Microsoft.Extensions.Logging.Abstractions;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Storage.Infrastructure.UnreferencedFiles;

namespace Modules.Storage.UnitTests;

/// <summary>
/// Spec 2026-11-18 («Lifecycle»), plan Task 11: el barrido diario también suelta el archivo de un bloque «Mensaje» que ya nadie usa. Es el
/// mismo recorrido del de las imágenes de plantilla (<see cref="UnreferencedFileSweep"/>) con otro dueño (<see cref="FileOwnerType.ChatbotMedia"/>),
/// otra pregunta (<see cref="IChatbotMediaReferences"/>: chatbots, versiones, plantillas guardadas y mensajes de la bandeja) y otro motivo.
/// </summary>
public sealed class ChatbotMediaSweepTests
{
    private static readonly Guid TenantA = Guid.CreateVersion7();
    private static readonly Guid TenantB = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 3, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUnreferencedFiles _files = new();
    private readonly FakeFileReferences _references = new();
    private readonly FailingPublicObjectStorage _publicStorage = new();
    private readonly SweepAuditPublisher _audit = new();

    [Fact]
    public async Task AnUnreferencedChatbotFileOlderThanADayIsReleasedWithItsReason()
    {
        var file = _files.Seed(TenantA, FileOwnerType.ChatbotMedia, Now.AddDays(-2));
        var recent = _files.Seed(TenantA, FileOwnerType.ChatbotMedia, Now.AddHours(-23));
        var templateImage = _files.Seed(TenantA, FileOwnerType.MessageTemplate, Now.AddDays(-2));

        var released = await NewSweep().SweepAsync(Now, Ct);

        Assert.Equal(1, released);
        Assert.Equal(Now.AddHours(-24), _files.LastCreatedBefore);
        Assert.Equal(FileResourceStatus.Deleted, file.Status);
        Assert.Equal(FileResourceStatus.Available, recent.Status);
        Assert.Equal(FileResourceStatus.Available, templateImage.Status);
        Assert.Contains($"tenants/{TenantA:N}/media/{file.Id.Value:N}/original.png", _publicStorage.DeletedKeys);
        var audit = Assert.Single(_audit.Entries);
        Assert.Equal(
            ((Guid?)TenantA, "storage.file.deleted", "file", file.Id.ToString(), "chatbot_media_unreferenced", Now),
            (audit.TenantId, audit.Action, audit.ResourceType, audit.ResourceId, audit.Outcome, audit.OccurredAt));
        Assert.Equal([file.Id], _files.Committed);
        Assert.All(_references.Calls, call => Assert.DoesNotContain(templateImage.Id.Value, call.FileIds));
    }

    [Fact]
    public async Task AReferencedOneIsKept()
    {
        var file = _files.Seed(TenantA, FileOwnerType.ChatbotMedia, Now.AddDays(-30));
        _references.Reference(TenantA, file.Id.Value);

        var released = await NewSweep().SweepAsync(Now, Ct);

        Assert.Equal(0, released);
        Assert.Equal(FileResourceStatus.Available, file.Status);
        Assert.Empty(_files.Claimed);
        Assert.Empty(_publicStorage.DeletedKeys);
        Assert.Empty(_audit.Entries);
    }

    // Falla cerrado: sin saber qué usa Automations (o la bandeja) no se suelta nada de ese tenant; los demás siguen.
    [Fact]
    public async Task AFailingReferenceQueryKeepsTheTenantsFiles()
    {
        var fileA = _files.Seed(TenantA, FileOwnerType.ChatbotMedia, Now.AddDays(-2));
        var fileB = _files.Seed(TenantB, FileOwnerType.ChatbotMedia, Now.AddDays(-2));
        _references.FailingTenant = TenantA;

        var released = await NewSweep().SweepAsync(Now, Ct);

        Assert.Equal(1, released);
        Assert.Equal(FileResourceStatus.Available, fileA.Status);
        Assert.Equal(FileResourceStatus.Deleted, fileB.Status);
        Assert.DoesNotContain(fileA.Id, _files.Claimed);
    }

    // Un chatbot que se guardó (o un mensaje que se mandó) entre la pregunta de la página y el reclamo: bajo el candado se vuelve a
    // preguntar y el archivo se queda.
    [Fact]
    public async Task TheRecheckUnderTheLockKeepsAFileReferencedMeanwhile()
    {
        var file = _files.Seed(TenantA, FileOwnerType.ChatbotMedia, Now.AddDays(-2));
        _references.ReferenceAfterFirstAsk(TenantA, file.Id.Value);

        var released = await NewSweep().SweepAsync(Now, Ct);

        Assert.Equal(0, released);
        Assert.Equal(FileResourceStatus.Available, file.Status);
        Assert.Equal([file.Id], _files.Claimed);
        Assert.Equal([file.Id], _files.Abandoned);
        Assert.Empty(_files.Committed);
        Assert.Empty(_publicStorage.DeletedKeys);
        Assert.Empty(_audit.Entries);
    }

    private ChatbotMediaSweep NewSweep() =>
        new(
            _files,
            _references,
            new UnreferencedFileReleaser(
                _files, new FilePublication(_publicStorage, new FixedClock(Now)), _audit, NullLogger<UnreferencedFileReleaser>.Instance),
            NullLogger<ChatbotMediaSweep>.Instance);
}
