using System.Text.Json;
using Bootstrapper.Copilot;
using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.DependencyInjection;
using Modules.Ai.Application;
using Modules.Ai.Domain;
using Modules.Authorization.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Plan fase 2, C2.4 (spec 2026-11-17 copiloto D-C3): cada herramienta corre como el miembro que pregunta, con sus permisos y la máscara de
/// módulos resueltos <b>en cada llamada</b>, y la consulta se despacha en un scope con el contexto delegado. Nunca como el principal de
/// servicio de los agentes, nunca con el tenant de un argumento.
/// </summary>
public sealed class CopilotToolExecutorTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid OtherTenant = Guid.Parse("01900000-0000-7000-8000-000000000002");
    private static readonly Guid Member = Guid.Parse("01900000-0000-7000-8000-0000000000b1");
    private static readonly CopilotPrincipal Principal = new(Tenant, Member);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly MemberPermissions _authorization = new();
    private readonly SwitchableTenantModules _modules = new();

    [Fact]
    public async Task ToolsAreOfferedOnlyWithPermissionAndModuleNow()
    {
        _authorization.Permissions = ["reporting.orders.read", "catalog.product.read"];
        _modules.Set = TenantModuleSet.FromStored(TenantModuleKeys.All.Where(key => key != TenantModuleKeys.Catalog));

        var available = await Executor().ListAvailableAsync(Principal, Ct);

        // Con permiso y módulo: who_am_i. Con permiso sin módulo: nada de catálogo. Sin permiso: nada de margen.
        Assert.Equal(["who_am_i"], available!.Select(tool => tool.Name));
    }

    [Fact]
    public async Task TheAvailableToolsAreSortedByName()
    {
        _authorization.Permissions = ["reporting.orders.read", "catalog.product.read", "reporting.margin.read"];

        var available = await Executor().ListAvailableAsync(Principal, Ct);

        Assert.Equal(["margin_probe", "product_probe", "who_am_i"], available!.Select(tool => tool.Name));
    }

    // Un tenant con todos los módulos ve más de 20 herramientas. No se recorta: el tope de 20 de Anthropic es de las estrictas, y las del
    // copiloto no lo son.
    [Fact]
    public async Task MoreThanTwentyToolsAreAllOffered()
    {
        _authorization.Permissions = ["reporting.orders.read"];
        var many = Enumerable.Range(1, 25).Select(index => new ProbeTool($"probe_{index:D2}", "reporting.orders.read", TenantModuleKeys.Reporting));
        var executor = new CopilotToolExecutor(Services().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), [.. many]);

        var available = await executor.ListAvailableAsync(Principal, Ct);

        Assert.Equal(25, available!.Count);
    }

    [Fact]
    public async Task WithoutAnActiveMembershipThereIsNoList()
    {
        _authorization.Permissions = null;

        Assert.Null(await Executor().ListAvailableAsync(Principal, Ct));
    }

    // D-C3: lo que el miembro perdió entre dos llamadas, el copiloto también lo pierde. El modelo recibe el error, nunca el dato.
    [Fact]
    public async Task APermissionLostBetweenCallsReturnsNoPermission()
    {
        var executor = Executor();
        _authorization.Permissions = ["reporting.orders.read"];
        var first = await executor.ExecuteAsync(Principal, "who_am_i", "{}", Ct);
        _authorization.Permissions = [];
        var second = await executor.ExecuteAsync(Principal, "who_am_i", "{}", Ct);

        Assert.False(first.IsError);
        Assert.Equal((true, CopilotToolErrors.NoPermission), (second.IsError, second.ErrorCode));
        Assert.DoesNotContain(Member.ToString(), second.ResultJson, StringComparison.Ordinal);
        Assert.Empty(second.Refs);
        Assert.Equal(2, _authorization.Resolved);
    }

    [Fact]
    public async Task AModuleTurnedOffBetweenCallsReturnsNoPermission()
    {
        var executor = Executor();
        _authorization.Permissions = ["reporting.orders.read"];
        await executor.ExecuteAsync(Principal, "who_am_i", "{}", Ct);
        _modules.Set = TenantModuleSet.FromStored(TenantModuleKeys.All.Where(key => key != TenantModuleKeys.Reporting));

        var result = await executor.ExecuteAsync(Principal, "who_am_i", "{}", Ct);

        Assert.Equal(CopilotToolErrors.NoPermission, result.ErrorCode);
    }

    [Fact]
    public async Task AnInactiveMemberGetsMemberInactive()
    {
        _authorization.Permissions = null;

        var result = await Executor().ExecuteAsync(Principal, "who_am_i", "{}", Ct);

        Assert.Equal(CopilotToolErrors.MemberInactive, result.ErrorCode);
    }

    [Fact]
    public async Task TheDispatchedQueryRunsAsTheMemberAndNeverAsTheServicePrincipal()
    {
        _authorization.Permissions = ["reporting.orders.read"];

        var result = await Executor().ExecuteAsync(Principal, "who_am_i", "{}", Ct);

        var seen = JsonSerializer.Deserialize<WhoAmI>(result.ResultJson, Web)!;
        Assert.Equal((Member, Tenant, true, false, nameof(DelegatedExecutionContext)), (seen.SubjectId, seen.TenantId, seen.CanReadOrders, seen.CanReadMargin, seen.ContextType));
        // El principal de servicio es un conjunto de capacidades, no un sujeto: ninguna capacidad suya llega como permiso.
        Assert.DoesNotContain(AiServicePrincipal.Capabilities, capability => seen.Permissions.Contains(capability));
        Assert.Equal([(Member, Tenant)], _authorization.Asked);
    }

    // D-C15: el tenant sale del hilo. Un tenantId en los argumentos no cambia nada.
    [Fact]
    public async Task TheTenantComesFromThePrincipalNeverFromArguments()
    {
        _authorization.Permissions = ["reporting.orders.read"];

        var result = await Executor().ExecuteAsync(Principal, "who_am_i", $$"""{"tenantId":"{{OtherTenant}}"}""", Ct);

        var seen = JsonSerializer.Deserialize<WhoAmI>(result.ResultJson, Web)!;
        Assert.Equal(Tenant, seen.TenantId);
        Assert.DoesNotContain(_authorization.Asked, asked => asked.Tenant == OtherTenant);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"fail":"argument"}""")]
    [InlineData("""{"fail":"validation"}""")]
    public async Task InvalidArgumentsComeBackAsAToolError(string arguments)
    {
        _authorization.Permissions = ["reporting.orders.read"];

        var result = await Executor().ExecuteAsync(Principal, "who_am_i", arguments, Ct);

        Assert.Equal((true, AgentToolErrors.InvalidArguments), (result.IsError, result.ErrorCode));
    }

    [Fact]
    public async Task AForbiddenOrMissingRecordComesBackAsAToolError()
    {
        _authorization.Permissions = ["reporting.orders.read"];

        var forbidden = await Executor().ExecuteAsync(Principal, "who_am_i", """{"fail":"forbidden"}""", Ct);
        var missing = await Executor().ExecuteAsync(Principal, "who_am_i", """{"fail":"missing"}""", Ct);

        Assert.Equal(CopilotToolErrors.NoPermission, forbidden.ErrorCode);
        Assert.Equal(CopilotToolErrors.NotFound, missing.ErrorCode);
    }

    [Fact]
    public async Task AnUnknownToolIsAToolError()
    {
        _authorization.Permissions = ["reporting.orders.read"];

        var result = await Executor().ExecuteAsync(Principal, "drop_database", "{}", Ct);

        Assert.Equal(AgentToolErrors.UnknownTool, result.ErrorCode);
    }

    // D-C3 con la resolución real: AuthorizationService y MembershipDirectory son scoped y MembershipDirectory memoiza la membresía por
    // scope. El trabajo del copiloto corre entero en un scope, así que resolver con los servicios de ese scope dejaba los permisos y la
    // membresía de la primera llamada para todas las siguientes.
    [Fact]
    public async Task EachCallResolvesPermissionsInAFreshScope()
    {
        _authorization.Permissions = ["reporting.orders.read"];
        var services = Services();
        services.AddScoped<IAuthorizationService>(_ => new ScopeMemoizedPermissions(_authorization));
        using var provider = services.BuildServiceProvider();
        using var jobScope = provider.CreateScope();
        var executor = new CopilotToolExecutor(jobScope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(), Tools());

        var first = await executor.ExecuteAsync(Principal, "who_am_i", "{}", Ct);
        _authorization.Permissions = null;
        var second = await executor.ExecuteAsync(Principal, "who_am_i", "{}", Ct);

        Assert.False(first.IsError);
        Assert.Equal(CopilotToolErrors.MemberInactive, second.ErrorCode);
    }

    // Cada llamada en su scope: el contexto delegado de una no queda en el de la siguiente ni en el scope de quien llama.
    [Fact]
    public async Task EachCallGetsItsOwnDelegatedScope()
    {
        _authorization.Permissions = ["reporting.orders.read"];
        using var provider = Services().BuildServiceProvider();
        using var outer = provider.CreateScope();
        var executor = new CopilotToolExecutor(provider.GetRequiredService<IServiceScopeFactory>(), Tools());

        await executor.ExecuteAsync(Principal, "who_am_i", "{}", Ct);

        Assert.Null(outer.ServiceProvider.GetRequiredService<DelegatedExecutionScope>().Context);
        Assert.Equal("HttpExecutionContext", outer.ServiceProvider.GetRequiredService<IExecutionContext>().GetType().Name);
    }

    private CopilotToolExecutor Executor() =>
        new(Services().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), Tools());

    private ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAuthorizationService>(_authorization);
        services.AddSingleton<ITenantModules>(_modules);
        services.AddHttpContextAccessor();
        services.AddQepExecutionContext();
        services.AddScoped<IRequestDispatcher, Bootstrapper.Messaging.RequestDispatcher>();
        services.AddScoped<IQueryHandler<WhoAmIQuery, WhoAmI>, WhoAmIHandler>();
        return services;
    }

    private static ICopilotTool[] Tools() =>
    [
        new WhoAmITool(),
        new ProbeTool("product_probe", "catalog.product.read", TenantModuleKeys.Catalog),
        new ProbeTool("margin_probe", "reporting.margin.read", TenantModuleKeys.Reporting),
    ];

    public sealed record WhoAmIQuery(Guid TenantId, string? Fail) : IQuery<WhoAmI>;

    public sealed record WhoAmI(
        Guid SubjectId, Guid TenantId, bool CanReadOrders, bool CanReadMargin, string ContextType, IReadOnlyList<string> Permissions);

    /// <summary>Un handler como los reales: lee a quien llama sólo de <see cref="IExecutionContext"/>.</summary>
    public sealed class WhoAmIHandler(IExecutionContext context) : IQueryHandler<WhoAmIQuery, WhoAmI>
    {
        public Task<WhoAmI> HandleAsync(WhoAmIQuery query, CancellationToken cancellationToken) =>
            query.Fail switch
            {
                "validation" => throw new ValidationException([new ValidationFailure("to", "to must be on or after from.")]),
                "forbidden" => throw new RequestForbiddenException("authorization.denied", "Denied."),
                "missing" => throw new ResourceNotFoundException("orders.order.not_found", "Missing."),
                _ => Task.FromResult(new WhoAmI(
                    context.SubjectId,
                    context.TenantId.Value,
                    context.HasPermission("reporting.orders.read"),
                    context.HasPermission("reporting.margin.read"),
                    context.GetType().Name,
                    [.. AiServicePrincipal.Capabilities.Where(context.HasPermission)])),
            };
    }

    private sealed class WhoAmITool : ICopilotTool
    {
        public CopilotToolContract Contract { get; } = new(
            new AiToolDefinition("who_am_i", "Prueba.", """{"type":"object","properties":{},"required":[],"additionalProperties":false}"""),
            "reporting.orders.read",
            TenantModuleKeys.Reporting);

        public async Task<CopilotToolResult> ExecuteAsync(CopilotToolCall call, CancellationToken cancellationToken)
        {
            var fail = call.Arguments.TryGetProperty("fail", out var value) ? value.GetString() : null;
            if (fail == "argument")
            {
                throw new CopilotArgumentException("fail");
            }

            var seen = await call.Dispatcher.QueryAsync(new WhoAmIQuery(call.Principal.TenantId, fail), cancellationToken);
            return CopilotToolResult.Ok(seen, []);
        }
    }

    private sealed class ProbeTool(string name, string permission, TenantModuleKey module) : ICopilotTool
    {
        public CopilotToolContract Contract { get; } = new(
            new AiToolDefinition(name, "Prueba.", """{"type":"object","properties":{},"required":[],"additionalProperties":false}"""), permission, module);

        public Task<CopilotToolResult> ExecuteAsync(CopilotToolCall call, CancellationToken cancellationToken) =>
            Task.FromResult(CopilotToolResult.Ok(new { }, []));
    }

    private sealed class MemberPermissions : IAuthorizationService
    {
        public IReadOnlyCollection<string>? Permissions { get; set; } = [];

        public List<(Guid Subject, Guid Tenant)> Asked { get; } = [];

        public int Resolved => Asked.Count;

        public Task<AuthorizationDecision> AuthorizeAsync(Guid subjectId, Guid tenantId, string permission, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<string>?> ResolvePermissionsAsync(Guid subjectId, Guid tenantId, CancellationToken cancellationToken)
        {
            Asked.Add((subjectId, tenantId));
            return Task.FromResult(Permissions);
        }
    }

    /// <summary>Como <c>MembershipDirectory</c>: la primera resolución del scope vale para todo el scope.</summary>
    private sealed class ScopeMemoizedPermissions(MemberPermissions source) : IAuthorizationService
    {
        private bool _resolved;
        private IReadOnlyCollection<string>? _permissions;

        public Task<AuthorizationDecision> AuthorizeAsync(Guid subjectId, Guid tenantId, string permission, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async Task<IReadOnlyCollection<string>?> ResolvePermissionsAsync(Guid subjectId, Guid tenantId, CancellationToken cancellationToken)
        {
            if (!_resolved)
            {
                _permissions = await source.ResolvePermissionsAsync(subjectId, tenantId, cancellationToken);
                _resolved = true;
            }

            return _permissions;
        }
    }

    private sealed class SwitchableTenantModules : ITenantModules
    {
        public TenantModuleSet? Set { get; set; } = TenantModuleSet.FromStored(TenantModuleKeys.All);

        public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(tenantId == Tenant ? Set : null);
    }
}
