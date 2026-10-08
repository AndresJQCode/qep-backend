using BuildingBlocks.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>Spec 2026-10-07: el canal que el flujo de envío lee antes de decidir qué mostrar, con el
/// permiso de enviar (no el de leer Configuración). Sin fila, habilitado y Shared.</summary>
public sealed class GetWhatsAppChannelHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static (GetWhatsAppChannelHandler Handler, InMemoryTenantWhatsAppSettingsRepository Repository)
        NewHandler(IExecutionContext? executionContext = null)
    {
        var repository = new InMemoryTenantWhatsAppSettingsRepository();
        return (
            new GetWhatsAppChannelHandler(repository, executionContext ?? new StubExecutionContext(SubjectId, TenantId)),
            repository);
    }

    [Fact]
    public async Task WithoutARowItIsEnabledAndShared()
    {
        var (handler, _) = NewHandler();

        var dto = await handler.HandleAsync(new GetWhatsAppChannelQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.Equal(new WhatsAppChannelDto(true, "Shared"), dto);
    }

    [Theory]
    [InlineData(WhatsAppMode.Shared, true)]
    [InlineData(WhatsAppMode.Own, true)]
    [InlineData(WhatsAppMode.Disabled, false)]
    public async Task EnabledIsEveryModeButDisabled(WhatsAppMode mode, bool enabled)
    {
        var (handler, repository) = NewHandler();
        var row = TenantWhatsAppSettings.CreateEmpty(TenantId, Now);
        row.Configure(WhatsAppMode.Own, WhatsAppProvider.Zenvia, new ProtectedSecret("k1", [1]), null,
            "573001234567", "9b2f4c1e-3d5a-4e6b-8c7d-1a2b3c4d5e6f", Now);
        row.Configure(mode, null, null, null, null, null, Now);
        repository.Add(row);

        var dto = await handler.HandleAsync(new GetWhatsAppChannelQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.Equal(new WhatsAppChannelDto(enabled, mode.ToString()), dto);
    }

    [Fact]
    public async Task WithoutQuotationManageIsForbidden()
    {
        var (handler, _) = NewHandler(
            new StubExecutionContext(SubjectId, TenantId, QuotationsPermissions.QuotationManage));

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(new GetWhatsAppChannelQuery(TenantId), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ForAnotherTenantIsForbidden()
    {
        var (handler, _) = NewHandler(new StubExecutionContext(SubjectId, Guid.CreateVersion7()));

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(new GetWhatsAppChannelQuery(TenantId), TestContext.Current.CancellationToken));
    }
}
