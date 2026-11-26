using System.Reflection;
using BuildingBlocks.Application;
using Modules.Siigo.Application;
using Modules.Siigo.Application.Settings;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Siigo.UnitTests;

/// <summary>
/// Las listas de opciones que leen los selectores del asistente salen ordenadas por nombre: Siigo las entrega en el orden de su
/// base y el selector no debe depender de él.
/// </summary>
public sealed class ListSiigoOptionsHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ConnectionId = Guid.CreateVersion7();

    [Fact]
    public async Task TheTaxesComeOrderedAlphabeticallyByNameAndInactiveOnesAreLeftOut()
    {
        var api = ApiProxy.Create(
        [
            new SiigoTaxOption("3", "ReteICA 0.966%", "ReteICA", 0.966m, true),
            new SiigoTaxOption("1", "ReteFuente 4%", "Retefuente", 4m, true),
            new SiigoTaxOption("9", "Antigua", "Retefuente", 1m, false),
            new SiigoTaxOption("2", "autorretención 0.4%", "AutoRetefuente", 0.4m, true),
            new SiigoTaxOption("4", "IVA 19%", "IVA", 19m, true),
        ]);
        var handler = new ListSiigoOptionsHandler(new Context(), new AllModules(), new ActiveConnections(), api.Instance);

        var options = await handler.HandleAsync(
            new ListSiigoOptionsQuery(TenantId, ConnectionId, SiigoOptionLists.Taxes), TestContext.Current.CancellationToken);

        Assert.Equal(["autorretención 0.4%", "IVA 19%", "ReteFuente 4%", "ReteICA 0.966%"], options.Select(option => option.Name));
    }

    [Theory]
    [InlineData(SiigoOptionLists.DocumentTypes)]
    [InlineData(SiigoOptionLists.Users)]
    [InlineData(SiigoOptionLists.CostCenters)]
    [InlineData(SiigoOptionLists.Warehouses)]
    [InlineData(SiigoOptionLists.PaymentTypes)]
    public async Task EveryOtherListAlsoComesOrderedAlphabeticallyByName(string list)
    {
        var api = ApiProxy.Create(
            [],
            options: [new SiigoOption("1", "Zeta"), new SiigoOption("2", "alfa"), new SiigoOption("3", "Beta")],
            users: [new SiigoUser("1", "Zeta", null), new SiigoUser("2", "alfa", null), new SiigoUser("3", "Beta", null)]);
        var handler = new ListSiigoOptionsHandler(new Context(), new AllModules(), new ActiveConnections(), api.Instance);

        var options = await handler.HandleAsync(new ListSiigoOptionsQuery(TenantId, ConnectionId, list), TestContext.Current.CancellationToken);

        Assert.Equal(["alfa", "Beta", "Zeta"], options.Select(option => option.Name));
    }

    private sealed class Context : IExecutionContext
    {
        public Guid SubjectId { get; } = Guid.CreateVersion7();

        public TenantId TenantId { get; } = new(ListSiigoOptionsHandlerTests.TenantId);

        public bool HasPermission(string permission) => true;
    }

    private sealed class AllModules : ITenantModules
    {
        public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<TenantModuleSet?>(null);
    }

    private sealed class ActiveConnections : ISiigoConnectionDirectory
    {
        public Task<SiigoConnectionInfo?> FindAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            Task.FromResult<SiigoConnectionInfo?>(new SiigoConnectionInfo(connectionId, "Siigo", "Active"));

        public Task<SiigoConnectionResolution> ResolveForSetupAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            Task.FromResult(new SiigoConnectionResolution(new SiigoCredentials(connectionId, "user", "key"), null));

        public Task<SiigoConnectionResolution> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SiigoConnectionInfo>> ListByTenantAsync(Guid tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>Un <see cref="ISiigoApi"/> que sólo sabe listar; cualquier otra llamada es un error de la prueba.</summary>
    public class ApiProxy : DispatchProxy
    {
        private IReadOnlyList<SiigoTaxOption> _taxes = [];
        private IReadOnlyList<SiigoOption> _options = [];
        private IReadOnlyList<SiigoUser> _users = [];

        public ISiigoApi Instance => (ISiigoApi)(object)this;

        public static ApiProxy Create(
            IReadOnlyList<SiigoTaxOption> taxes, IReadOnlyList<SiigoOption>? options = null, IReadOnlyList<SiigoUser>? users = null)
        {
            var proxy = (ApiProxy)(object)DispatchProxy.Create<ISiigoApi, ApiProxy>();
            proxy._taxes = taxes;
            proxy._options = options ?? [];
            proxy._users = users ?? [];
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(ISiigoApi.ListTaxesAsync) => Task.FromResult(_taxes),
            nameof(ISiigoApi.ListUsersAsync) => Task.FromResult(_users),
            nameof(ISiigoApi.ListDocumentTypesAsync) or nameof(ISiigoApi.ListCostCentersAsync)
                or nameof(ISiigoApi.ListWarehousesAsync) or nameof(ISiigoApi.ListPaymentTypesAsync) => Task.FromResult(_options),
            _ => throw new NotSupportedException(targetMethod?.Name),
        };
    }
}
