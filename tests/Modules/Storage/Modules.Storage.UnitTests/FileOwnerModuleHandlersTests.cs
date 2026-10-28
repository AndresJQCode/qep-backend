using BuildingBlocks.Application;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Storage.UnitTests;

/// <summary>
/// Spec 2026-10-07, «Archivos de Storage»: los siete handlers que cargan un archivo por id. Un
/// comprobante con <c>orders</c> apagado da 403 <c>tenancy.module_not_enabled</c> **sin efecto**
/// —ni bucket, ni auditoría, ni <c>SaveChangesAsync</c>— y un archivo de otro tenant sigue dando 404
/// antes del guard. Los dobles «intocables» lanzan si se los usa, así que un guard puesto tarde
/// aparece como otra excepción. En publicar, despublicar y borrar, el error tiene que ser el de módulo
/// y no el de <c>PaymentProofGuard</c>.
/// </summary>
public sealed class FileOwnerModuleHandlersTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> Handlers =>
    [
        "IssueDownloadUrl", "CompleteUpload", "CancelUpload", "UpdateFileMetadata",
        "PublishFile", "UnpublishFile", "SoftDeleteFile",
    ];

    [Theory]
    [MemberData(nameof(Handlers))]
    public async Task APaymentProofWithOrdersOffIsForbiddenWithoutAnyEffect(string handler)
    {
        var effects = new Effects();
        var modules = FixedTenantModules.AllBut(TenantModuleKeys.Orders);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => Run(
            handler, PaymentProofFor(handler), TenantId, modules, effects));

        Assert.Equal("tenancy.module_not_enabled", error.Code);
        Assert.Equal([TenantId], modules.Asked);
        Assert.Empty(effects.Audit.Actions);
        Assert.Equal(0, effects.UnitOfWork.Saves);
        Assert.Empty(effects.PublicStorage.Copies);
        Assert.Empty(effects.PublicStorage.DeletedKeys);
        Assert.Empty(effects.Probe.Asked);
    }

    [Theory]
    [MemberData(nameof(Handlers))]
    public async Task AFileOfAnotherTenantIsStillNotFoundBeforeTheModule(string handler)
    {
        var modules = FixedTenantModules.AllBut(TenantModuleKeys.Orders);

        await Assert.ThrowsAsync<ResourceNotFoundException>(() => Run(
            handler, PaymentProofFor(handler), Guid.CreateVersion7(), modules, new Effects()));

        Assert.Empty(modules.Asked);
    }

    // La prueba de control: con orders prendido, cada handler llega a su propia regla (y ahí sí
    // tocaría el bucket, por eso lanza otra cosa). Confirma que el 403 de arriba es el del módulo.
    [Theory]
    [MemberData(nameof(Handlers))]
    public async Task WithOrdersOnTheModuleDoesNotStopIt(string handler)
    {
        var error = await Record.ExceptionAsync(() => Run(
            handler, PaymentProofFor(handler), TenantId, FixedTenantModules.AllBut(), new Effects()));

        Assert.False(
            error is RequestForbiddenException { Code: "tenancy.module_not_enabled" },
            $"{handler} stopped on the module with orders on.");
    }

    private static FileResource PaymentProofFor(string handler)
    {
        var proof = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, Guid.CreateVersion7(), FileOwnerType.PaymentProof,
            "comprobante.pdf", "application/pdf", 2048, $"staging/tenants/{TenantId:N}/comprobante", Now);
        if (handler != "CompleteUpload")
        {
            proof.CompleteUpload("checksum", 2048, Now);
            proof.MarkClean(Now);
        }

        return proof;
    }

    private static Task Run(string handler, FileResource resource, Guid tenantId, ITenantModules modules, Effects effects)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new InMemoryFileResourceRepository(resource);
        var clock = new FixedClock(Now);
        var context = new AllowAllExecutionContext(tenantId);
        var publication = new FilePublication(effects.PublicStorage, clock);
        var fileId = resource.Id.Value;

        return handler switch
        {
            "IssueDownloadUrl" => new IssueDownloadUrlHandler(
                    repository, modules, new UntouchableObjectStorage(), effects.PublicStorage,
                    effects.UnitOfWork, effects.Audit, context, clock)
                .HandleAsync(new IssueDownloadUrlCommand(tenantId, fileId), cancellationToken),
            "CompleteUpload" => new CompleteUploadHandler(
                    repository, modules, new UntouchableObjectStorage(), new UntouchableContentInspector(),
                    new UntouchableVariantGenerator(), new UntouchablePaymentProofProcessor(), new UntouchableScanner(),
                    effects.UnitOfWork, effects.Audit, context, clock)
                .HandleAsync(new CompleteUploadCommand(tenantId, fileId), cancellationToken),
            "CancelUpload" => new CancelUploadHandler(
                    repository, modules, new UntouchableObjectStorage(), effects.UnitOfWork, effects.Audit, context, clock)
                .HandleAsync(new CancelUploadCommand(tenantId, fileId), cancellationToken),
            "UpdateFileMetadata" => new UpdateFileMetadataHandler(
                    repository, modules, effects.UnitOfWork, effects.Audit, context, clock)
                .HandleAsync(new UpdateFileMetadataCommand(tenantId, fileId, "comprobantes", []), cancellationToken),
            "PublishFile" => new PublishFileHandler(
                    repository, modules, effects.UnitOfWork, publication, effects.PublicStorage, effects.Audit,
                    context, clock)
                .HandleAsync(new PublishFileCommand(tenantId, fileId), cancellationToken),
            "UnpublishFile" => new UnpublishFileHandler(
                    repository, modules, effects.UnitOfWork, publication, effects.PublicStorage, [effects.Probe],
                    effects.Audit, context, clock)
                .HandleAsync(new UnpublishFileCommand(tenantId, fileId), cancellationToken),
            "SoftDeleteFile" => new SoftDeleteFileHandler(
                    repository, modules, effects.UnitOfWork, publication, [effects.Probe], effects.Audit, context, clock)
                .HandleAsync(new SoftDeleteFileCommand(tenantId, fileId), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(handler), handler, null),
        };
    }

    private sealed class Effects
    {
        public RecordingStorageAuditPublisher Audit { get; } = new();

        public CountingStorageUnitOfWork UnitOfWork { get; } = new();

        public RecordingPublicObjectStorage PublicStorage { get; } = new();

        // Referenciado: sin el guard de módulo, despublicar y borrar pararían acá con invalid_state.
        public StubFileReferenceProbe Probe { get; } = new(referenced: true);
    }
}
