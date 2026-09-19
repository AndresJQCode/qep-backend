using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

/// <summary>
/// El orden entre Storage y el commit de Tenancy al asignar y quitar el logo (decisiones 7 y 8
/// del spec 2026-09-19): qué se publica antes, qué se retira después, y que ningún retiro que
/// corre después de un commit dependa del token del request.
/// </summary>
public sealed class TenantLogoHandlerTests
{
    private const string OldPublicKey = "tenants/x/media/old/original.png";
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly List<string> _steps = [];
    private readonly Tenant _tenant;
    private readonly RecordingTenantLogoStorage _storage;
    private readonly RecordingTenancyUnitOfWork _unitOfWork;

    public TenantLogoHandlerTests()
    {
        _tenant = Tenant.Create(
            TenantId.New(), "qcode-demo", "QCode Demo", "es-CO", "America/Bogota", "yyyy-MM-dd", Now);
        _storage = new RecordingTenantLogoStorage(_steps);
        _unitOfWork = new RecordingTenancyUnitOfWork(_steps);
    }

    // Un 412 no deja copias públicas huérfanas: la versión se compara antes de tocar Storage.
    [Fact]
    public async Task AStaleVersionIsRejectedWithoutPublishing()
    {
        var command = new SetTenantLogoCommand(
            _tenant.Id, Guid.CreateVersion7(), _tenant.Version + 1, "corr-1");

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            SetHandler().HandleAsync(command, TestContext.Current.CancellationToken));

        Assert.Equal("concurrency.conflict", error.Code);
        Assert.Empty(_storage.Calls);
        Assert.Equal(0, _unitOfWork.Commits);
    }

    [Fact]
    public async Task AssigningTheCurrentLogoAgainDoesNotTouchStorage()
    {
        var fileId = GiveTheTenantALogo();

        var settings = await SetHandler().HandleAsync(
            SetCommand(fileId), TestContext.Current.CancellationToken);

        Assert.Empty(_storage.Calls);
        Assert.Empty(_steps);
        Assert.Equal(fileId, settings.Logo!.FileId);
    }

    // Decisión 7 del spec: el logo nuevo ya está commiteado, así que un retiro fallido del viejo
    // no falla el request. TryUnpublishAsync nunca lanza (el adaptador lo prueba en
    // TenantLogoStorageTests); acá se fija que el handler no dependa de su resultado.
    [Fact]
    public async Task AFailedCleanupOfTheOldLogoDoesNotFailTheRequest()
    {
        var oldFileId = GiveTheTenantALogo();
        var newFileId = Guid.CreateVersion7();
        _storage.TryUnpublishFails = true;

        var settings = await SetHandler().HandleAsync(
            SetCommand(newFileId), TestContext.Current.CancellationToken);

        Assert.Equal(["publish:" + newFileId, "commit", "try-unpublish-failed:" + oldFileId], _steps);
        Assert.Equal(newFileId, settings.Logo!.FileId);
        Assert.Equal(1, _unitOfWork.Commits);
    }

    // El agregado rechazó la asignación después de que Storage ya commiteó la publicación: el
    // retiro del archivo nuevo no puede depender del token del request, que puede estar cancelado.
    [Fact]
    public async Task ARejectedAssignmentRetiresTheNewFileWithoutTheRequestToken()
    {
        _storage.PublicKey = "";
        var newFileId = Guid.CreateVersion7();
        using var request = new CancellationTokenSource();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            SetHandler().HandleAsync(SetCommand(newFileId), request.Token));

        var cleanup = Assert.Single(_storage.Calls, call => call.Method == "try-unpublish");
        Assert.Equal(newFileId, cleanup.FileId);
        Assert.Equal(CancellationToken.None, cleanup.Token);
    }

    // El commit de Tenancy falló: se retira el archivo nuevo (sin el token del request) y la
    // excepción que ve la persona es la original de SaveChangesAsync, no una de Storage.
    [Fact]
    public async Task ACommitFailureRetiresTheNewFileAndRethrowsTheOriginalException()
    {
        var failure = new RequestConcurrencyException("concurrency.conflict", "EF concurrency clash.");
        _unitOfWork.Failure = failure;
        var newFileId = Guid.CreateVersion7();
        using var request = new CancellationTokenSource();

        var thrown = await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            SetHandler().HandleAsync(SetCommand(newFileId), request.Token));

        Assert.Same(failure, thrown);
        Assert.Equal(["publish:" + newFileId, "commit", "try-unpublish:" + newFileId], _steps);
        Assert.Equal(CancellationToken.None, _storage.Calls[^1].Token);
    }

    // Reemplazar: el logo viejo se retira estrictamente después del commit del nuevo (decisión 7)
    // y sin el token del request, porque Tenancy ya commiteó.
    [Fact]
    public async Task ReplacingRetiresTheOldLogoAfterTheCommitWithoutTheRequestToken()
    {
        var oldFileId = GiveTheTenantALogo();
        var newFileId = Guid.CreateVersion7();
        using var request = new CancellationTokenSource();

        await SetHandler().HandleAsync(SetCommand(newFileId), request.Token);

        Assert.Equal(["publish:" + newFileId, "commit", "try-unpublish:" + oldFileId], _steps);
        Assert.Equal(CancellationToken.None, _storage.Calls[^1].Token);
    }

    // Quitar: el agregado valida primero (EnsureActive dentro de RemoveLogo), después se retira la
    // copia pública y recién después se commitea Tenancy (decisión 8).
    [Fact]
    public async Task RemovingUpdatesTheTenantBeforeUnpublishingAndUnpublishesBeforeTheCommit()
    {
        var fileId = GiveTheTenantALogo();
        Guid? logoAtUnpublish = fileId;
        _storage.OnUnpublish = () => logoAtUnpublish = _tenant.LogoFileId;

        var settings = await RemoveHandler().HandleAsync(
            new RemoveTenantLogoCommand(_tenant.Id, _tenant.Version, "corr-1"),
            TestContext.Current.CancellationToken);

        Assert.Null(logoAtUnpublish);
        Assert.Equal(["unpublish:" + fileId, "commit"], _steps);
        Assert.Null(settings.Logo);
    }

    private Guid GiveTheTenantALogo()
    {
        var fileId = Guid.CreateVersion7();
        _tenant.SetLogo(fileId, OldPublicKey, Now);
        _tenant.PullDomainEvents();
        return fileId;
    }

    private SetTenantLogoCommand SetCommand(Guid fileId) =>
        new(_tenant.Id, fileId, _tenant.Version, "corr-1");

    private SetTenantLogoHandler SetHandler() =>
        new(
            new InMemoryTenantRepository(_tenant),
            _unitOfWork,
            new SettingsUpdateExecutionContext(_tenant.Id),
            new RecordingAuditRecorder(),
            new RecordingOutboxWriter(),
            new FixedClock(Now),
            _storage,
            new SetTenantLogoValidator());

    private RemoveTenantLogoHandler RemoveHandler() =>
        new(
            new InMemoryTenantRepository(_tenant),
            _unitOfWork,
            new SettingsUpdateExecutionContext(_tenant.Id),
            new RecordingAuditRecorder(),
            new RecordingOutboxWriter(),
            new FixedClock(Now),
            _storage,
            new RemoveTenantLogoValidator());
}
