using BuildingBlocks.Application;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Domain;

namespace Modules.Storage.UnitTests;

/// <summary>Spec 2026-10-07, «Archivos de Storage: el dueño decide el módulo».</summary>
public sealed class FileOwnerModulesTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    // Un miembro nuevo de FileOwnerType sin mapear no pasa: criterio de éxito 4.
    [Fact]
    public void EveryOwnerTypeIsMapped()
    {
        Assert.Equal(
            Enum.GetValues<FileOwnerType>().Order(),
            FileOwnerModules.ByOwnerType.Keys.Order());
    }

    // El mapa se lee en cada request y no cambia nunca: congelado, no un Dictionary mutable.
    [Fact]
    public void TheMapIsFrozen()
    {
        Assert.IsType<System.Collections.Frozen.FrozenDictionary<FileOwnerType, TenantModuleKey?>>(
            FileOwnerModules.ByOwnerType, exactMatch: false);
    }

    [Fact]
    public void ProductsAreCatalogProofsAreOrdersTheBannerIsTheStoreAndTheRestIsCore()
    {
        Assert.Same(TenantModuleKeys.Catalog, FileOwnerModules.ModuleOf(FileOwnerType.Product));
        Assert.Same(TenantModuleKeys.Orders, FileOwnerModules.ModuleOf(FileOwnerType.PaymentProof));
        Assert.Same(TenantModuleKeys.Store, FileOwnerModules.ModuleOf(FileOwnerType.Store));
        Assert.Same(TenantModuleKeys.Support, FileOwnerModules.ModuleOf(FileOwnerType.SupportTicket));
        foreach (var core in new[] { FileOwnerType.User, FileOwnerType.Entity, FileOwnerType.System, FileOwnerType.Tenant })
        {
            Assert.Null(FileOwnerModules.ModuleOf(core));
        }
    }

    // Spec 2026-11-17 (imagen de encabezado de plantilla): la imagen es de Mensajería.
    [Fact]
    public void AMessageTemplateFileBelongsToMessaging()
    {
        Assert.Equal(TenantModuleKeys.Messaging, FileOwnerModules.ModuleOf(FileOwnerType.MessageTemplate));
    }

    // Sólo llega por la sesión de Mensajería: la consola de Storage no la ve ni la crea.
    [Fact]
    public void AMessageTemplateFileIsSystemOwned()
    {
        Assert.True(SystemOwnedFiles.Includes(FileOwnerType.MessageTemplate));
    }

    // Spec 2026-11-18 (multimedia del bloque «Mensaje»): el archivo es de los chatbots.
    [Fact]
    public void AChatbotMediaFileBelongsToAutomations()
    {
        Assert.Equal(TenantModuleKeys.Automations, FileOwnerModules.ModuleOf(FileOwnerType.ChatbotMedia));
    }

    // Sólo llega por la sesión del editor de chatbots: la consola de Storage no lo ve ni lo crea.
    [Fact]
    public void AChatbotMediaFileIsSystemOwned()
    {
        Assert.True(SystemOwnedFiles.Includes(FileOwnerType.ChatbotMedia));
    }

    // Spec 2026-11-20 (RRHH): el PDF de un contrato o de un registro es de Recursos humanos.
    [Fact]
    public void AnHrDocumentBelongsToHr()
    {
        Assert.Equal(TenantModuleKeys.Hr, FileOwnerModules.ModuleOf(FileOwnerType.HrDocument));
    }

    // Sólo llega por la sesión de Hr (hr.contract.manage): la consola de Storage no lo ve ni lo crea.
    [Fact]
    public void AnHrDocumentIsSystemOwned()
    {
        Assert.True(SystemOwnedFiles.Includes(FileOwnerType.HrDocument));
    }

    // owner_type es character varying(20) y se persiste por nombre: un nombre más largo no cabe.
    [Fact]
    public void TheOwnerTypeNameFitsItsColumn()
    {
        Assert.All(Enum.GetNames<FileOwnerType>(), name => Assert.True(name.Length <= 20, name));
    }

    [Fact]
    public void WmsEvidenceAndExportsBelongToTheWmsModule()
    {
        Assert.Same(TenantModuleKeys.Wms, FileOwnerModules.ModuleOf(FileOwnerType.WmsEvidence));
        Assert.Same(TenantModuleKeys.Wms, FileOwnerModules.ModuleOf(FileOwnerType.WmsExport));
        Assert.Equal(
            [FileOwnerType.WmsEvidence, FileOwnerType.WmsExport],
            FileOwnerModules.OwnerTypesDisabledIn(TenantModuleSet.FromStored(
                TenantModuleKeys.All.Except([TenantModuleKeys.Wms]))).Order());
    }

    // I-2: sólo el export de WMS pide un permiso además de storage.file.read; el resto de tipos no pide nada más.
    [Fact]
    public void OnlyTheWmsExportRequiresAnExtraPermission()
    {
        Assert.Equal("wms.stock.export", FileOwnerModules.RequiredPermissionOf(FileOwnerType.WmsExport));
        Assert.Equal([FileOwnerType.WmsExport], FileOwnerModules.RequiredPermissionByOwnerType.Keys);
        Assert.Null(FileOwnerModules.RequiredPermissionOf(FileOwnerType.WmsEvidence));
        Assert.Equal([FileOwnerType.WmsExport], FileOwnerModules.OwnerTypesLackingPermission(_ => false));
        Assert.Empty(FileOwnerModules.OwnerTypesLackingPermission(_ => true));
    }

    // Un miembro nuevo de FileOwnerType no pasa sin decidir las dos cosas: si un miembro lo sube por POST /files y si otro módulo puede
    // referenciarlo. Dueño de sistema, generado por el servidor y con permiso extra son las tres razones para que no.
    [Fact]
    public void EveryOwnerTypeIsUploadableByMembersOrDeliberatelyNot()
    {
        Assert.Equal([FileOwnerType.WmsExport], ServerGeneratedFiles.OwnerTypes);
        Assert.Equal(
            [FileOwnerType.User, FileOwnerType.Entity, FileOwnerType.System, FileOwnerType.Product, FileOwnerType.PaymentProof,
                FileOwnerType.Tenant, FileOwnerType.Store, FileOwnerType.WmsEvidence],
            Enum.GetValues<FileOwnerType>().Except(SystemOwnedFiles.OwnerTypes).Except(ServerGeneratedFiles.OwnerTypes).Order());
    }

    [Fact]
    public void SystemOwnedAndPermissionedFilesAreNotReferenceableByOtherModules()
    {
        Assert.Equal(
            [FileOwnerType.User, FileOwnerType.Entity, FileOwnerType.System, FileOwnerType.Product, FileOwnerType.PaymentProof,
                FileOwnerType.Tenant, FileOwnerType.Store, FileOwnerType.WmsEvidence],
            Enum.GetValues<FileOwnerType>().Where(FileOwnerModules.IsReferenceableByOtherModules).Order());
    }

    [Fact]
    public void OwnerTypesDisabledInListsTheOnesWhoseModuleIsOff()
    {
        // store depende de orders: apagar orders apaga también el banner de la tienda.
        Assert.Equal(
            [FileOwnerType.PaymentProof, FileOwnerType.Store],
            FileOwnerModules.OwnerTypesDisabledIn(TenantModuleSet.FromStored(
                TenantModuleKeys.All.Except([TenantModuleKeys.Orders]))).Order());
        // support no tiene dependencias: sólo sale con su propio módulo apagado.
        Assert.Equal(
            [FileOwnerType.Product, FileOwnerType.PaymentProof, FileOwnerType.Store, FileOwnerType.SupportTicket,
                FileOwnerType.MessageTemplate, FileOwnerType.ChatbotMedia, FileOwnerType.HrDocument, FileOwnerType.WmsEvidence,
                FileOwnerType.WmsExport],
            FileOwnerModules.OwnerTypesDisabledIn(TenantModuleSet.Empty).Order());
        Assert.Empty(FileOwnerModules.OwnerTypesDisabledIn(TenantModuleSet.FromStored(TenantModuleKeys.All)));
    }

    // Con catalog apagado el listado excluye a los productos, y por las dependencias orders -> catalog, store -> orders y
    // wms -> catalog también a los comprobantes, al banner de la tienda y a los archivos de bodega: el módulo apagado se propaga.
    [Fact]
    public void OwnerTypesDisabledInExcludesProductsWhenCatalogIsOff()
    {
        Assert.Equal(
            [FileOwnerType.Product, FileOwnerType.PaymentProof, FileOwnerType.Store, FileOwnerType.WmsEvidence,
                FileOwnerType.WmsExport],
            FileOwnerModules.OwnerTypesDisabledIn(TenantModuleSet.FromStored(
                TenantModuleKeys.All.Except([TenantModuleKeys.Catalog]))).Order());
    }

    [Theory]
    [InlineData(FileOwnerType.Product, "catalog")]
    [InlineData(FileOwnerType.PaymentProof, "orders")]
    [InlineData(FileOwnerType.Store, "store")]
    [InlineData(FileOwnerType.SupportTicket, "support")]
    public async Task AFileWhoseModuleIsOffIsForbidden(FileOwnerType ownerType, string module)
    {
        var modules = FixedTenantModules.AllBut(TenantModuleKey.Parse(module));

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            FileOwnerModuleGuard.EnsureOwnerModuleEnabledAsync(
                modules, FileOf(ownerType), TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.module_not_enabled", error.Code);
        Assert.Equal([TenantId], modules.Asked);
    }

    [Theory]
    [InlineData(FileOwnerType.Tenant)]
    [InlineData(FileOwnerType.User)]
    public async Task ACoreFilePassesWithNoModules(FileOwnerType ownerType)
    {
        var modules = new FixedTenantModules(TenantModuleSet.Empty);

        await FileOwnerModuleGuard.EnsureOwnerModuleEnabledAsync(
            modules, FileOf(ownerType), TestContext.Current.CancellationToken);

        Assert.Empty(modules.Asked);
    }

    // Preflight F-03: sin la aserción, la prueba pasaría aunque el guard nunca consultara el puerto.
    [Fact]
    public async Task ASimulatedTenantIsNotBlocked()
    {
        var modules = FixedTenantModules.Simulated;

        await FileOwnerModuleGuard.EnsureOwnerModuleEnabledAsync(
            modules, FileOf(FileOwnerType.PaymentProof), TestContext.Current.CancellationToken);

        Assert.Equal([TenantId], modules.Asked);
    }

    private static FileResource FileOf(FileOwnerType ownerType) =>
        FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, Guid.CreateVersion7(), ownerType,
            "archivo.pdf", "application/pdf", 2048, $"staging/tenants/{TenantId:N}/archivo", Now);
}
