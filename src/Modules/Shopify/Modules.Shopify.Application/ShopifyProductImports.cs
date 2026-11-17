using System.Text.Json;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Audit.Domain;
using Modules.Shopify.Domain;
using Modules.Tenancy.Application;

namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-11-17 §7: la última importación de una tienda, como la pinta la pantalla («Importando 340 de 1.200»). Plan D3:
/// <see cref="Total"/>, <see cref="Done"/> y <see cref="Created"/> cuentan <b>productos</b>; <see cref="Matched"/>,
/// <see cref="WithoutSku"/> y <see cref="Skipped"/> cuentan <b>variantes</b> (una falla de producto suma todas las suyas a
/// <see cref="Skipped"/>). Los enums viajan con su nombre. <see cref="Issues"/> trae hasta 50, en el orden en que pasaron.
/// </summary>
public sealed record ShopifyProductImportDto(
    Guid Id,
    string Mode,
    string Status,
    int Total,
    int Done,
    int Created,
    int Matched,
    int WithoutSku,
    int Skipped,
    string? FailureCode,
    IReadOnlyList<ShopifyProductImportIssueDto> Issues);

public sealed record ShopifyProductImportIssueDto(long ShopifyProductId, string Title, string Code);

/// <summary>El cuerpo del <c>POST</c>. Todo opcional en el binding para que un modo ausente sea un 422 con <c>errors</c>.
/// <see cref="RedirectUri"/> (plan D6) es a dónde vuelve la reautorización si falta <c>read_products</c>.</summary>
public sealed record StartShopifyProductImportRequest(string? Mode, string? RedirectUri);

/// <summary>El 202 del <c>POST</c>.</summary>
public sealed record ShopifyProductImportStarted(Guid ImportId);

/// <summary><c>POST …/stores/{connectionId}/products/import</c> (<c>integrations.connection.manage</c>).</summary>
public sealed record StartShopifyProductImportCommand(Guid TenantId, Guid ConnectionId, string? Mode, string? RedirectUri)
    : ICommand<ShopifyProductImportStarted>;

/// <summary><c>GET …/stores/{connectionId}/products/import</c> (<c>integrations.connection.read</c>): la última, o <c>null</c>.</summary>
public sealed record GetShopifyProductImportQuery(Guid TenantId, Guid ConnectionId) : IQuery<ShopifyProductImportDto?>;

/// <summary>El modo es <c>All</c> o <c>Missing</c>, sin distinguir mayúsculas; nunca un número (que <c>Enum.TryParse</c> sí aceptaría).</summary>
public sealed class StartShopifyProductImportValidator : AbstractValidator<StartShopifyProductImportCommand>
{
    public StartShopifyProductImportValidator()
    {
        RuleFor(command => command.Mode)
            .Must(mode => ShopifyProductImportModes.TryParse(mode, out _))
            .WithMessage("The mode must be All or Missing.")
            .OverridePropertyName("mode");
    }
}

internal static class ShopifyProductImportModes
{
    public static bool TryParse(string? value, out ShopifyProductImportMode mode)
    {
        foreach (var candidate in Enum.GetValues<ShopifyProductImportMode>())
        {
            if (string.Equals(value?.Trim(), candidate.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                mode = candidate;
                return true;
            }
        }

        mode = default;
        return false;
    }
}

/// <summary>
/// El orden de las guardas: tenant, permiso y módulo (403) → validador (422 <c>validation.failed</c>) → conexión del tenant (403) →
/// no operativa o sin token (422 <c>shopify.store.not_operational</c>) → tienda sin empresa (422 <c>company_required</c>, P4) → sin
/// <c>read_products</c> (422 <c>scope_missing</c>, con la URL de reautorización si vino <c>redirectUri</c>) → membresía activa (403).
/// El 409 de una importación viva lo da el índice único al guardar, nunca una consulta previa; el interceptor timbra la cola.
/// </summary>
public sealed class StartShopifyProductImportHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IShopifyConnectionDirectory connections,
    IShopifyStoreRepository stores,
    IShopifyReauthorization reauthorization,
    IMembershipDirectory memberships,
    IShopifyProductImportRepository imports,
    IShopifyUnitOfWork unitOfWork,
    IShopifyAuditRecorder auditRecorder,
    IClock clock,
    IValidator<StartShopifyProductImportCommand> validator) : ICommandHandler<StartShopifyProductImportCommand, ShopifyProductImportStarted>
{
    public async Task<ShopifyProductImportStarted> HandleAsync(StartShopifyProductImportCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await ShopifyAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, ShopifyPermissions.ConnectionManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        if (!ShopifyProductImportModes.TryParse(command.Mode, out var mode))
        {
            // El validador ya lo rechazó: esto sólo evita un modo por defecto silencioso.
            throw new InvalidOperationException("The validated import mode could not be parsed.");
        }

        var connection = await ShopifyStoreSupport.ConnectionAsync(connections, command.TenantId, command.ConnectionId, cancellationToken);
        ShopifyProductGuards.EnsureOperational(connection);

        var store = await stores.FindAsync(command.TenantId, command.ConnectionId, cancellationToken);
        if (store?.Billing is null)
        {
            throw new ShopifyDomainException(
                ShopifyErrorCodes.ProductsCompanyRequired, "The Shopify store needs its issuing company before importing products.");
        }

        await ShopifyProductGuards.EnsureReadProductsAsync(reauthorization, connection, command.RedirectUri, cancellationToken);
        var requestedBy = await memberships.FindActiveMembershipIdAsync(executionContext.SubjectId, command.TenantId, cancellationToken)
            ?? throw ShopifyAuthorization.Denied();

        var now = clock.UtcNow;
        var import = ShopifyProductImport.Request(command.TenantId, command.ConnectionId, mode, requestedBy, executionContext.SubjectId, now);
        imports.Add(import);
        auditRecorder.Record(
            command.TenantId,
            executionContext.SubjectId,
            AuditActorType.Human,
            ShopifyAuditActions.ProductImportRequested,
            ShopifyAuditActions.ProductImportResourceType,
            import.Id.ToString(),
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["connectionId"] = command.ConnectionId.ToString(),
                ["mode"] = mode.ToString(),
            }),
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new ShopifyProductImportStarted(import.Id);
    }
}

/// <summary>La última importación de la tienda con sus primeros 50 issues; <c>null</c> si nunca se importó.</summary>
public sealed class GetShopifyProductImportHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IShopifyConnectionDirectory connections,
    IShopifyProductImportRepository imports) : IQueryHandler<GetShopifyProductImportQuery, ShopifyProductImportDto?>
{
    public const int MaxIssues = 50;

    public async Task<ShopifyProductImportDto?> HandleAsync(GetShopifyProductImportQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await ShopifyAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, ShopifyPermissions.ConnectionRead, cancellationToken);
        await ShopifyStoreSupport.ConnectionAsync(connections, query.TenantId, query.ConnectionId, cancellationToken);

        var import = await imports.FindLatestAsync(query.TenantId, query.ConnectionId, cancellationToken);
        if (import is null)
        {
            return null;
        }

        var issues = await imports.ListIssuesAsync(import.Id, MaxIssues, cancellationToken);
        return new ShopifyProductImportDto(
            import.Id,
            import.Mode.ToString(),
            import.Status.ToString(),
            import.Total,
            import.Done,
            import.Created,
            import.Matched,
            import.WithoutSku,
            import.Skipped,
            import.FailureCode,
            [.. issues.Select(issue => new ShopifyProductImportIssueDto(issue.ShopifyProductId, issue.Title, issue.Code))]);
    }
}
