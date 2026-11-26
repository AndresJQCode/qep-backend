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

    private static readonly string[] HandlerNames =
    [
        "IssueDownloadUrl", "CompleteUpload", "CancelUpload", "UpdateFileMetadata",
        "PublishFile", "UnpublishFile", "SoftDeleteFile",
    ];

    public static TheoryData<string> Handlers => [.. HandlerNames];

    // Cada handler contra cada tipo de dueño que pide un permiso extra: un tipo nuevo en esa tabla entra solo.
    public static TheoryData<string, FileOwnerType> HandlersByPermissionedOwner
    {
        get
        {
            var data = new TheoryData<string, FileOwnerType>();
            foreach (var ownerType in FileOwnerModules.RequiredPermissionByOwnerType.Keys)
            {
                foreach (var handler in HandlerNames)
                {
                    data.Add(handler, ownerType);
                }
            }

            return data;
        }
    }

    // Un handler nuevo que use el repositorio de archivos tiene que entrar a HandlerNames, y con eso queda bajo las reglas de esta
    // clase. Las dos excepciones no cargan por id: el listado filtra en SQL y la sesión de subida crea el archivo.
    [Fact]
    public void EveryHandlerThatUsesTheFileRepositoryIsCovered()
    {
        var handlers = typeof(IssueDownloadUrlHandler).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false }
                && type.GetInterfaces().Any(contract => contract.IsGenericType
                    && (contract.GetGenericTypeDefinition() == typeof(ICommandHandler<,>)
                        || contract.GetGenericTypeDefinition() == typeof(IQueryHandler<,>)))
                && type.GetConstructors().Any(constructor => constructor.GetParameters()
                    .Any(parameter => parameter.ParameterType == typeof(IFileResourceRepository))))
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal);
        string[] expected = [.. HandlerNames.Select(name => $"{name}Handler"), "CreateUploadSessionHandler", "ListFilesHandler"];

        Assert.Equal(expected.Order(StringComparer.Ordinal), handlers);
    }

    // I-2: storage.file.* es núcleo; sin el permiso del dueño, 403 authorization.denied y ningún efecto, en las siete rutas por id.
    [Theory]
    [MemberData(nameof(HandlersByPermissionedOwner))]
    public async Task WithoutTheOwnerPermissionTheFileIsForbiddenWithoutAnyEffect(string handler, FileOwnerType ownerType)
    {
        var effects = new Effects();
        var context = new ExecutionContextLacking(TenantId, FileOwnerModules.RequiredPermissionOf(ownerType)!);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => Run(
            handler, PermissionedFileFor(handler, ownerType), TenantId, FixedTenantModules.AllBut(), effects, context));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Empty(effects.Audit.Actions);
        Assert.Equal(0, effects.UnitOfWork.Saves);
        Assert.Empty(effects.PublicStorage.Copies);
        Assert.Empty(effects.PublicStorage.DeletedKeys);
        Assert.Empty(effects.Probe.Asked);
    }

    // Mismo orden que en IssueDownloadUrl: primero el 404 de otro tenant, después el módulo y recién ahí el permiso del dueño.
    [Theory]
    [MemberData(nameof(HandlersByPermissionedOwner))]
    public async Task TheNotFoundAndTheModuleGoBeforeTheOwnerPermission(string handler, FileOwnerType ownerType)
    {
        var otherTenant = Guid.CreateVersion7();
        var permission = FileOwnerModules.RequiredPermissionOf(ownerType)!;

        await Assert.ThrowsAsync<ResourceNotFoundException>(() => Run(
            handler, PermissionedFileFor(handler, ownerType), otherTenant, FixedTenantModules.AllBut(), new Effects(),
            new ExecutionContextLacking(otherTenant, permission)));
        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => Run(
            handler, PermissionedFileFor(handler, ownerType), TenantId,
            FixedTenantModules.AllBut(FileOwnerModules.ModuleOf(ownerType)!), new Effects(),
            new ExecutionContextLacking(TenantId, permission)));

        Assert.Equal("tenancy.module_not_enabled", error.Code);
    }

    // La prueba de control: con el permiso, cada handler sigue hasta su propia regla.
    [Theory]
    [MemberData(nameof(HandlersByPermissionedOwner))]
    public async Task WithTheOwnerPermissionTheGuardDoesNotStopIt(string handler, FileOwnerType ownerType)
    {
        var error = await Record.ExceptionAsync(() => Run(
            handler, PermissionedFileFor(handler, ownerType), TenantId, FixedTenantModules.AllBut(), new Effects()));

        Assert.False(
            error is RequestForbiddenException { Code: "authorization.denied" },
            $"{handler} denied a caller that holds the owner permission.");
    }

    // El export lo genera el servidor (WmsExportStorage): ni con todos los permisos lo crea POST /files. Mismo 422 que un tipo que no
    // existe.
    [Fact]
    public async Task AMemberCannotOpenAnUploadForAWmsExport()
    {
        var unitOfWork = new CountingStorageUnitOfWork();
        var handler = new CreateUploadSessionHandler(
            new InMemoryFileResourceRepository(), new UntouchableObjectStorage(), unitOfWork,
            new AllowAllExecutionContext(TenantId), new FixedClock(Now));

        var error = await Assert.ThrowsAsync<StorageDomainException>(() => handler.HandleAsync(
            new CreateUploadSessionCommand(TenantId, TenantId, FileOwnerType.WmsExport, "saldos.pdf", "application/pdf", 10),
            TestContext.Current.CancellationToken));

        Assert.Equal("storage.file.owner_type_invalid", error.Code);
        Assert.Equal(0, unitOfWork.Saves);
    }

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

    // Fix round 1 del Bloque D: un archivo de dueño de sistema (SupportTicket) no existe para la consola de Storage. Con todos los
    // permisos y todos los módulos, cada handler responde el mismo 404 que a un id de otro tenant, sin tocar nada.
    [Theory]
    [MemberData(nameof(Handlers))]
    public async Task ASystemOwnedFileIsNotFoundWithoutAnyEffect(string handler)
    {
        var effects = new Effects();
        var modules = FixedTenantModules.AllBut();

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() => Run(
            handler, SupportAttachmentFor(handler), TenantId, modules, effects));

        Assert.Equal("storage.file.not_found", error.Code);
        Assert.Empty(modules.Asked);
        Assert.Empty(effects.Audit.Actions);
        Assert.Equal(0, effects.UnitOfWork.Saves);
        Assert.Empty(effects.PublicStorage.Copies);
        Assert.Empty(effects.Probe.Asked);
    }

    [Fact]
    public void SupportAttachmentsAndTemplateImagesAreTheSystemOwnedFiles()
    {
        Assert.Equal(
            [FileOwnerType.SupportTicket, FileOwnerType.MessageTemplate, FileOwnerType.ChatbotMedia, FileOwnerType.HrDocument],
            SystemOwnedFiles.OwnerTypes.Order());
        Assert.True(SystemOwnedFiles.Includes(FileOwnerType.SupportTicket));
        Assert.False(SystemOwnedFiles.Includes(FileOwnerType.User));
    }

    [Fact]
    public async Task AMemberCannotOpenAnUploadForASystemOwner()
    {
        var unitOfWork = new CountingStorageUnitOfWork();
        var handler = new CreateUploadSessionHandler(
            new InMemoryFileResourceRepository(), new UntouchableObjectStorage(), unitOfWork,
            new AllowAllExecutionContext(TenantId), new FixedClock(Now));

        var error = await Assert.ThrowsAsync<StorageDomainException>(() => handler.HandleAsync(
            new CreateUploadSessionCommand(TenantId, Guid.NewGuid(), FileOwnerType.SupportTicket, "acta.pdf", "application/pdf", 10),
            TestContext.Current.CancellationToken));

        Assert.Equal("storage.file.owner_type_invalid", error.Code);
        Assert.Equal(0, unitOfWork.Saves);
    }

    private static FileResource SupportAttachmentFor(string handler)
    {
        var attachment = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, Guid.NewGuid(), FileOwnerType.SupportTicket,
            "acta.png", "image/png", 2048, $"staging/tenants/{TenantId:N}/acta", Now);
        if (handler is not ("CompleteUpload" or "CancelUpload"))
        {
            attachment.CompleteUpload("checksum", 2048, Now);
            attachment.Promote($"files/tenants/{TenantId:N}/acta", Now);
        }

        return attachment;
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

    // Como el comprobante: pendiente para las dos rutas de subida y disponible para las demás.
    private static FileResource PermissionedFileFor(string handler, FileOwnerType ownerType)
    {
        var file = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, TenantId, ownerType,
            "saldos.pdf", "application/pdf", 2048, $"staging/tenants/{TenantId:N}/saldos", Now);
        if (handler is not ("CompleteUpload" or "CancelUpload"))
        {
            file.CompleteUpload("checksum", 2048, Now);
            file.MarkClean(Now);
        }

        return file;
    }

    private static Task Run(
        string handler, FileResource resource, Guid tenantId, ITenantModules modules, Effects effects,
        IExecutionContext? executionContext = null)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new InMemoryFileResourceRepository(resource);
        var clock = new FixedClock(Now);
        var context = executionContext ?? new AllowAllExecutionContext(tenantId);
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
                    new UntouchableVariantGenerator(), new UntouchablePaymentProofProcessor(),
                    new UntouchableChatbotMediaProcessing(), new UntouchableScanner(),
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
