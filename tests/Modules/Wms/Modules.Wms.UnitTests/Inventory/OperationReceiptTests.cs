using BuildingBlocks.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Wms.Api;
using Modules.Wms.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Inventory;

namespace Modules.Wms.UnitTests.Inventory;

/// <summary>El filtro de idempotencia (S2) sin host: cada prueba arma un <see cref="DefaultHttpContext"/> con las tres dependencias
/// que el filtro resuelve de <c>RequestServices</c>.</summary>
public sealed class OperationReceiptTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string K1 = "5b7c1f0e-7a56-4c0a-9d2e-1f0a9c6e4b01";
    private const string Path = "/api/v1/tenants/11111111-1111-1111-1111-111111111111/wms/adjustments";

    private sealed record Body(string Note) : IIdempotentRequest;

    private sealed record VersionedBody(long Version) : IVersionedRequest;

    private sealed class FakeExecutionContext(Guid tenantId) : IExecutionContext
    {
        public Guid SubjectId => Guid.Empty;

        public TenantId TenantId { get; } = new(tenantId);

        public bool HasPermission(string permission) => true;
    }

    private sealed class FakeReceipts : IOperationReceipts
    {
        public Dictionary<string, OperationReceipt> Stored { get; } = [];

        public int FindCalls { get; private set; }

        public Task<OperationReceipt?> FindAsync(Guid tenantId, string key, CancellationToken cancellationToken)
        {
            FindCalls++;
            return Task.FromResult(Stored.GetValueOrDefault(key));
        }

        public void Record<TResponse>(TResponse response) => throw new NotSupportedException();

        public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) => Task.FromResult(0);

        public void Store(string key, string hash, string json) =>
            Stored[key] = OperationReceipt.Create(Tenant, key, hash, json, DateTimeOffset.UtcNow);
    }

    private sealed record Harness(DefaultHttpContext Http, FakeReceipts Receipts, IdempotencyScope Scope);

    private static Harness Arrange(Guid? caller = null, Guid? routeTenant = null, string? header = null, Guid? routeId = null)
    {
        var receipts = new FakeReceipts();
        var scope = new IdempotencyScope();
        var services = new ServiceCollection()
            .AddSingleton<IOperationReceipts>(receipts)
            .AddSingleton(scope)
            .AddSingleton<IExecutionContext>(new FakeExecutionContext(caller ?? Tenant))
            .BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Method = "POST";
        http.Request.Path = Path;
        http.Request.RouteValues["tenantId"] = (routeTenant ?? Tenant).ToString();
        if (routeId is not null)
        {
            http.Request.RouteValues["id"] = routeId.Value.ToString();
        }

        if (header is not null)
        {
            http.Request.Headers["Idempotency-Key"] = header;
        }

        return new Harness(http, receipts, scope);
    }

    private static IdempotencyKeyFilter HeaderFilter() => new(IdempotencyKeySource.Header, null, null);

    private static Task<object?> RunAsync(
        IdempotencyKeyFilter filter, Harness harness, object? argument, Func<ValueTask<object?>>? next = null) =>
        filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(harness.Http, argument), _ => next?.Invoke() ?? ValueTask.FromResult<object?>("ran")).AsTask();

    [Fact]
    public async Task AKeyLongerThan128IsRejected()
    {
        var harness = Arrange(header: new string('k', 129));

        var failure = await Assert.ThrowsAsync<PreconditionRequiredException>(() => RunAsync(HeaderFilter(), harness, new Body("x")));

        Assert.Equal(WmsErrorCodes.IdempotencyKeyRequired, failure.Code);
        Assert.Equal(0, harness.Receipts.FindCalls);
    }

    // M-1: el spec pide `Idempotency-Key: <uuid>`. Una clave libre como la que deriva el servidor (`adjustment:<id>:post:3`) podría
    // bloquear 45 días la acción de un documento, porque las dos viven en el mismo espacio de `operation_receipts`.
    [Theory]
    [InlineData("k1")]
    [InlineData("adjustment:3f2504e0-4f89-41d3-9a0c-0305e82c3301:post:3")]
    [InlineData("3f2504e0-4f89-41d3-9a0c-0305e82c330")]
    public async Task AKeyThatIsNotAUuidIsRejectedBeforeAnyReceiptLookup(string key)
    {
        var harness = Arrange(header: key);

        var failure = await Assert.ThrowsAsync<PreconditionRequiredException>(() => RunAsync(HeaderFilter(), harness, new Body("x")));

        Assert.Equal(WmsErrorCodes.IdempotencyKeyRequired, failure.Code);
        Assert.Equal(0, harness.Receipts.FindCalls);
    }

    [Fact]
    public async Task AnEmptyKeyIsRejected()
    {
        var harness = Arrange(header: string.Empty);

        var failure = await Assert.ThrowsAsync<PreconditionRequiredException>(() => RunAsync(HeaderFilter(), harness, new Body("x")));

        Assert.Equal(WmsErrorCodes.IdempotencyKeyRequired, failure.Code);
    }

    [Fact]
    public async Task AMissingHeaderIsRejected()
    {
        var harness = Arrange();

        await Assert.ThrowsAsync<PreconditionRequiredException>(() => RunAsync(HeaderFilter(), harness, new Body("x")));
    }

    [Fact]
    public void TheDomainReceiptRejectsAnEmptyOrOversizedKey()
    {
        var hash = new string('a', 64);

        Assert.Throws<ArgumentException>(() => OperationReceipt.Create(Tenant, " ", hash, "{}", DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => OperationReceipt.Create(Tenant, new string('k', 129), hash, "{}", DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task AnotherTenantIsDeniedBeforeTheReceiptIsLooked()
    {
        var harness = Arrange(caller: Other, header: K1);
        harness.Receipts.Store(K1, RequestHasher.Hash("POST", Path, new Body("x")), "{\"id\":1}");

        var failure = await Assert.ThrowsAsync<RequestForbiddenException>(() => RunAsync(HeaderFilter(), harness, new Body("x")));

        Assert.Equal("authorization.denied", failure.Code);
        Assert.Equal(0, harness.Receipts.FindCalls);
        Assert.False(harness.Scope.IsActive);
    }

    [Fact]
    public async Task ARepeatedKeyWithTheSameBodyReplaysTheStoredResponseWith200()
    {
        var harness = Arrange(header: K1);
        harness.Receipts.Store(K1, RequestHasher.Hash("POST", Path, new Body("x")), "{\"id\":7}");
        var ran = false;

        var result = await RunAsync(HeaderFilter(), harness, new Body("x"), () =>
        {
            ran = true;
            return ValueTask.FromResult<object?>("ran");
        });

        var content = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.ContentHttpResult>(result);
        Assert.Equal(200, content.StatusCode);
        Assert.Equal("{\"id\":7}", content.ResponseContent);
        Assert.Equal("application/json", content.ContentType);
        Assert.False(ran);
    }

    [Fact]
    public async Task ARepeatedKeyWithAnotherBodyIsAConflict()
    {
        var harness = Arrange(header: K1);
        harness.Receipts.Store(K1, RequestHasher.Hash("POST", Path, new Body("x")), "{}");

        var failure = await Assert.ThrowsAsync<ResourceConflictException>(() => RunAsync(HeaderFilter(), harness, new Body("y")));

        Assert.Equal(WmsErrorCodes.IdempotencyKeyReused, failure.Code);
    }

    [Fact]
    public async Task AFreshKeyArmsTheScopeAndRunsTheHandler()
    {
        var harness = Arrange(header: K1);

        var result = await RunAsync(HeaderFilter(), harness, new Body("x"));

        Assert.Equal("ran", result);
        Assert.True(harness.Scope.IsActive);
        Assert.Equal(K1, harness.Scope.Key);
        Assert.Equal(Tenant, harness.Scope.TenantId);
        Assert.Equal(IdempotencyKeySource.Header, harness.Scope.Source);
        Assert.Equal(RequestHasher.Hash("POST", Path, new Body("x")), harness.Scope.RequestHash);
    }

    [Fact]
    public async Task TheServerModeDerivesTheKeyAndIgnoresTheHeader()
    {
        var id = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var harness = Arrange(header: "ignored", routeId: id);
        var filter = new IdempotencyKeyFilter(IdempotencyKeySource.Server, "adjustment", "post");

        await RunAsync(filter, harness, new VersionedBody(3));

        Assert.Equal($"adjustment:{id:D}:post:3", harness.Scope.Key);
        Assert.Equal(IdempotencyKeySource.Server, harness.Scope.Source);
        // La versión ya viaja en la clave: el hash no lleva cuerpo, así que repetir nunca da 409.
        Assert.Equal(RequestHasher.Hash("POST", Path, null), harness.Scope.RequestHash);
    }

    [Fact]
    public async Task TheServerModeWithoutAVersionedBodyIsAWiringError()
    {
        var harness = Arrange(routeId: Guid.NewGuid());
        var filter = new IdempotencyKeyFilter(IdempotencyKeySource.Server, "adjustment", "post");

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(filter, harness, new Body("x")));
    }

    [Fact]
    public async Task ALostRaceRereadsTheWinnersReceiptAndReplaysIt()
    {
        var harness = Arrange(header: K1);
        var hash = RequestHasher.Hash("POST", Path, new Body("x"));

        var result = await RunAsync(HeaderFilter(), harness, new Body("x"), () =>
        {
            // La ganadora commiteó entre la primera búsqueda y el guardado de esta petición.
            harness.Receipts.Store(K1, hash, "{\"id\":9}");
            throw new OperationReplayedException(Tenant, K1);
        });

        var content = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.ContentHttpResult>(result);
        Assert.Equal("{\"id\":9}", content.ResponseContent);
        Assert.Equal(2, harness.Receipts.FindCalls);
    }

    [Fact]
    public async Task ALoserFailingOnAConsumedPreconditionReplaysTheWinnersResponse()
    {
        var harness = Arrange(header: K1);
        var hash = RequestHasher.Hash("POST", Path, new Body("x"));

        // La ganadora consumió la precondición (versión, reverso, saldo): la perdedora falla con un error de negocio, no con la PK.
        var result = await RunAsync(HeaderFilter(), harness, new Body("x"), () =>
        {
            harness.Receipts.Store(K1, hash, "{\"id\":5}");
            throw new WmsDomainException(WmsErrorCodes.StockInsufficient, "not enough");
        });

        var content = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.ContentHttpResult>(result);
        Assert.Equal("{\"id\":5}", content.ResponseContent);
    }

    [Fact]
    public async Task AFailureWithAWinnersReceiptOfAnotherBodyIsAConflict()
    {
        var harness = Arrange(header: K1);

        var failure = await Assert.ThrowsAsync<ResourceConflictException>(() => RunAsync(HeaderFilter(), harness, new Body("x"), () =>
        {
            harness.Receipts.Store(K1, RequestHasher.Hash("POST", Path, new Body("other")), "{}");
            throw new WmsDomainException(WmsErrorCodes.StockInsufficient, "not enough");
        }));

        Assert.Equal(WmsErrorCodes.IdempotencyKeyReused, failure.Code);
    }

    [Fact]
    public async Task AFailureWithoutAReceiptKeepsItsOwnError()
    {
        var harness = Arrange(header: K1);

        await Assert.ThrowsAsync<WmsDomainException>(() => RunAsync(
            HeaderFilter(), harness, new Body("x"), () => throw new WmsDomainException(WmsErrorCodes.StockInsufficient, "not enough")));
    }

    [Fact]
    public async Task ACancelledRequestIsNeverReplayed()
    {
        var harness = Arrange(header: K1);
        harness.Receipts.Store(K1, RequestHasher.Hash("POST", Path, new Body("x")), "{}");

        // Con recibo ya guardado el filtro respondería antes de llegar al handler: se arma el caso por la ruta del catch.
        harness.Receipts.Stored.Clear();
        await Assert.ThrowsAsync<OperationCanceledException>(() => RunAsync(
            HeaderFilter(), harness, new Body("x"), () =>
            {
                harness.Receipts.Store(K1, RequestHasher.Hash("POST", Path, new Body("x")), "{}");
                throw new OperationCanceledException();
            }));
    }

    [Fact]
    public async Task ABodyThatFailedBindingSkipsTheFilter()
    {
        var harness = Arrange(routeId: Guid.NewGuid());
        harness.Http.Response.StatusCode = 400;
        var filter = new IdempotencyKeyFilter(IdempotencyKeySource.Server, "adjustment", "post");

        var result = await RunAsync(filter, harness, null);

        Assert.Equal("ran", result);
        Assert.Equal(0, harness.Receipts.FindCalls);
    }
}
