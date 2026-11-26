using Microsoft.Extensions.Logging.Abstractions;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Storage.Infrastructure.TemplateImages;
using Modules.Storage.Infrastructure.UnreferencedFiles;

namespace Modules.Storage.UnitTests;

/// <summary>
/// Spec 2026-11-17, «Closing the residual gaps»: la red final. Una vez al día, Storage toma las imágenes de encabezado vivas con más
/// de 24 h, le pregunta a Messaging (por tenant) cuáles sigue usando una plantilla y suelta el resto: copias públicas fuera, borrado
/// lógico y auditoría del sistema. La lectura y el reclamo de filas son SQL (los cubre integración); acá se fija qué decide el barrido.
/// </summary>
public sealed class TemplateHeaderImageSweepTests
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
    public async Task ASweepReleasesAnUnreferencedImage()
    {
        var file = _files.Seed(TenantA, FileOwnerType.MessageTemplate, Now.AddDays(-2));

        var released = await NewSweep().SweepAsync(Now, Ct);

        Assert.Equal(1, released);
        Assert.Equal(FileResourceStatus.Deleted, file.Status);
        Assert.Equal(Now, file.DeletedAt);
        Assert.Contains($"tenants/{TenantA:N}/media/{file.Id.Value:N}/original.png", _publicStorage.DeletedKeys);
        var audit = Assert.Single(_audit.Entries);
        Assert.Equal(
            ((Guid?)TenantA, "storage.file.deleted", "file", file.Id.ToString(), "template_header_image_unreferenced", Now),
            (audit.TenantId, audit.Action, audit.ResourceType, audit.ResourceId, audit.Outcome, audit.OccurredAt));
        Assert.Equal([file.Id], _files.Committed);
    }

    [Fact]
    public async Task ASweepKeepsAReferencedImage()
    {
        var file = _files.Seed(TenantA, FileOwnerType.MessageTemplate, Now.AddDays(-30));
        _references.Reference(TenantA, file.Id.Value);

        var released = await NewSweep().SweepAsync(Now, Ct);

        Assert.Equal(0, released);
        Assert.Equal(FileResourceStatus.Available, file.Status);
        Assert.Empty(_publicStorage.DeletedKeys);
        Assert.Empty(_audit.Entries);
        Assert.Empty(_files.Claimed);
    }

    // La gracia de 24 h aleja al barrido de una creación en vuelo: la imagen sube y la plantilla se guarda segundos después.
    [Fact]
    public async Task ASweepIgnoresFilesInsideTheGrace()
    {
        var file = _files.Seed(TenantA, FileOwnerType.MessageTemplate, Now.AddHours(-23));

        var released = await NewSweep().SweepAsync(Now, Ct);

        Assert.Equal(0, released);
        Assert.Equal(Now.AddHours(-24), _files.LastCreatedBefore);
        Assert.Equal(FileResourceStatus.Available, file.Status);
        Assert.Empty(_references.Calls);
        Assert.Empty(_publicStorage.DeletedKeys);
    }

    // La consulta ya filtra por dueño; el barrido no confía en eso: un archivo de otro dueño nunca se pregunta ni se suelta.
    [Fact]
    public async Task ASweepIgnoresOtherOwnerTypes()
    {
        var product = _files.Seed(TenantA, FileOwnerType.Product, Now.AddDays(-2));
        var tenantLogo = _files.Seed(TenantA, FileOwnerType.Tenant, Now.AddDays(-2));

        var released = await NewSweep().SweepAsync(Now, Ct);

        Assert.Equal(0, released);
        Assert.Equal(FileResourceStatus.Available, product.Status);
        Assert.Equal(FileResourceStatus.Available, tenantLogo.Status);
        Assert.Empty(_references.Calls);
        Assert.Empty(_files.Claimed);
    }

    [Fact]
    public async Task AFailureOnOneFileDoesNotStopTheSweep()
    {
        var failing = _files.Seed(TenantA, FileOwnerType.MessageTemplate, Now.AddDays(-3));
        var next = _files.Seed(TenantA, FileOwnerType.MessageTemplate, Now.AddDays(-2));
        _publicStorage.FailingFragment = failing.Id.Value.ToString("N");

        var released = await NewSweep().SweepAsync(Now, Ct);

        Assert.Equal(1, released);
        Assert.Equal(FileResourceStatus.Available, failing.Status);
        Assert.Equal(FileResourceStatus.Deleted, next.Status);
        Assert.Equal([failing.Id], _files.Abandoned);
        Assert.Equal([next.Id], _files.Committed);
        Assert.Equal([next.Id.ToString()], _audit.Entries.Select(entry => entry.ResourceId));
    }

    // Review Focus 5: la referencia de un tenant no protege ni suelta el archivo de otro. A cada tenant se le pregunta sólo por lo suyo.
    [Fact]
    public async Task ReferencesAreAskedPerTenant()
    {
        var fileA = _files.Seed(TenantA, FileOwnerType.MessageTemplate, Now.AddDays(-2));
        var fileB = _files.Seed(TenantB, FileOwnerType.MessageTemplate, Now.AddDays(-2));
        // TenantA «usa» el id del archivo de TenantB: no cuenta, porque a TenantA no se le pregunta por ese id.
        _references.Reference(TenantA, fileB.Id.Value);
        _references.Reference(TenantB, fileA.Id.Value);

        var released = await NewSweep().SweepAsync(Now, Ct);

        Assert.Equal(2, released);
        // La pregunta por página y la de cada archivo bajo el reclamo: todas con el tenant del archivo.
        Assert.All(_references.Calls.Where(call => call.TenantId == TenantA), call => Assert.Equal([fileA.Id.Value], call.FileIds));
        Assert.All(_references.Calls.Where(call => call.TenantId == TenantB), call => Assert.Equal([fileB.Id.Value], call.FileIds));
        Assert.Equal(2, _references.Calls.Count(call => call.TenantId == TenantA));
        Assert.Equal(2, _references.Calls.Count(call => call.TenantId == TenantB));
        Assert.Equal(FileResourceStatus.Deleted, fileA.Status);
        Assert.Equal(FileResourceStatus.Deleted, fileB.Status);
    }

    // Las imágenes vivas son las más viejas y no salen nunca del conjunto: si el barrido mirara sólo la primera página, más de 200
    // plantillas con imagen lo dejarían ciego para siempre. Recorre por cursor hasta el final.
    [Fact]
    public async Task ASweepReachesOrphansBehindAFullPageOfReferencedImages()
    {
        for (var index = 0; index < UnreferencedFileSweep.BatchSize; index++)
        {
            var live = _files.Seed(TenantA, FileOwnerType.MessageTemplate, Now.AddDays(-10).AddMinutes(index));
            _references.Reference(TenantA, live.Id.Value);
        }

        var orphan = _files.Seed(TenantA, FileOwnerType.MessageTemplate, Now.AddDays(-2));

        var released = await NewSweep().SweepAsync(Now, Ct);

        Assert.Equal(1, released);
        Assert.Equal(FileResourceStatus.Deleted, orphan.Status);
        Assert.All(_references.Calls, call => Assert.InRange(call.FileIds.Count, 1, UnreferencedFileSweep.BatchSize));
    }

    // Review Focus 4: otra réplica ya lo reclamó o lo soltó (el reclamo es FOR UPDATE SKIP LOCKED y relee el estado): no se audita dos veces.
    [Fact]
    public async Task AFileAnotherReplicaHoldsIsSkippedWithoutAuditing()
    {
        var file = _files.Seed(TenantA, FileOwnerType.MessageTemplate, Now.AddDays(-2));
        _files.HeldElsewhere.Add(file.Id);

        var released = await NewSweep().SweepAsync(Now, Ct);

        Assert.Equal(0, released);
        Assert.Equal(FileResourceStatus.Available, file.Status);
        Assert.Empty(_publicStorage.DeletedKeys);
        Assert.Empty(_audit.Entries);
    }

    // Revisión final: una creación que reusa un id viejo puede guardar su fila entre la pregunta de la página y el reclamo. Bajo el
    // candado se vuelve a preguntar por ese archivo, y si ya lo usa una plantilla no se suelta.
    [Fact]
    public async Task AFileReferencedAfterTheListingIsNotReleased()
    {
        var file = _files.Seed(TenantA, FileOwnerType.MessageTemplate, Now.AddDays(-2));
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

    // Sin saber qué usa Messaging no se suelta nada de ese tenant (falla cerrado); los demás tenants siguen.
    [Fact]
    public async Task AReferencesFailureReleasesNothingOfThatTenant()
    {
        var fileA = _files.Seed(TenantA, FileOwnerType.MessageTemplate, Now.AddDays(-2));
        var fileB = _files.Seed(TenantB, FileOwnerType.MessageTemplate, Now.AddDays(-2));
        _references.FailingTenant = TenantA;

        var released = await NewSweep().SweepAsync(Now, Ct);

        Assert.Equal(1, released);
        Assert.Equal(FileResourceStatus.Available, fileA.Status);
        Assert.Equal(FileResourceStatus.Deleted, fileB.Status);
    }

    private TemplateHeaderImageSweep NewSweep() =>
        new(
            _files,
            _references,
            new UnreferencedFileReleaser(
                _files, new FilePublication(_publicStorage, new FixedClock(Now)), _audit, NullLogger<UnreferencedFileReleaser>.Instance),
            NullLogger<TemplateHeaderImageSweep>.Instance);
}
