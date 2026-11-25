using BuildingBlocks.Application;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;

namespace Modules.Siigo.Application.Settings;

/// <summary>Las listas de Siigo que los selectores del asistente leen en vivo (spec 2026-11-18 §5.2): <c>document-types</c>,
/// <c>users</c>, <c>cost-centers</c>, <c>warehouses</c>, <c>payment-types</c> y <c>taxes</c>.</summary>
public static class SiigoOptionLists
{
    public const string DocumentTypes = "document-types";
    public const string Users = "users";
    public const string CostCenters = "cost-centers";
    public const string Warehouses = "warehouses";
    public const string PaymentTypes = "payment-types";
    public const string Taxes = "taxes";

    public static bool IsKnown(string? list) =>
        list is DocumentTypes or Users or CostCenters or Warehouses or PaymentTypes or Taxes;
}

/// <summary><c>GET …/options/{list}</c> (<c>siigo.settings.manage</c>).</summary>
public sealed record ListSiigoOptionsQuery(Guid TenantId, Guid ConnectionId, string List) : IQuery<IReadOnlyList<SiigoOptionDto>>;

/// <summary>
/// Spec 2026-11-18 §5.2: las opciones salen de Siigo en vivo, con la caché de 5 minutos por conexión del cliente HTTP (las listas no se
/// copian). En orden: tenant, permiso y módulo (403); la conexión de Siigo del tenant (403); la lista conocida (404); y las credenciales
/// de la conexión: una que no se puede usar (pausada, con la clave rechazada, ilegible) es 409 <c>siigo.connection_unavailable</c> con
/// el motivo en el mensaje. Un Siigo que no contesta es 503 <c>siigo.unavailable</c>, que lanza el cliente.
/// </summary>
public sealed class ListSiigoOptionsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISiigoConnectionDirectory connections,
    ISiigoApi api) : IQueryHandler<ListSiigoOptionsQuery, IReadOnlyList<SiigoOptionDto>>
{
    public async Task<IReadOnlyList<SiigoOptionDto>> HandleAsync(ListSiigoOptionsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await SiigoSettingsSupport.AuthorizeAsync(executionContext, tenantModules, query.TenantId, cancellationToken);
        await SiigoSettingsSupport.ConnectionAsync(connections, query.TenantId, query.ConnectionId, cancellationToken);
        if (!SiigoOptionLists.IsKnown(query.List))
        {
            throw new ResourceNotFoundException("siigo.options.list_unknown", "That Siigo list does not exist.");
        }

        var resolution = await connections.ResolveForSetupAsync(query.TenantId, query.ConnectionId, cancellationToken);
        if (resolution.Credentials is not { } credentials)
        {
            throw new ResourceConflictException(
                SiigoErrorCodes.ConnectionUnavailable,
                $"The Siigo connection cannot be used right now ({resolution.UnavailableReason}).");
        }

        if (query.List == SiigoOptionLists.Taxes)
        {
            return [.. (await api.ListTaxesAsync(credentials, cancellationToken))
                .Where(tax => tax.Active)
                .OrderBy(tax => tax.Name, StringComparer.InvariantCultureIgnoreCase)
                .Select(tax => new SiigoOptionDto(tax.Id, tax.Name, tax.Type, tax.Percentage))];
        }

        var options = query.List switch
        {
            SiigoOptionLists.DocumentTypes => await api.ListDocumentTypesAsync(credentials, cancellationToken),
            SiigoOptionLists.Users => [.. (await api.ListUsersAsync(credentials, refresh: false, cancellationToken)).Select(user => new SiigoOption(user.Id, user.Name))],
            SiigoOptionLists.CostCenters => await api.ListCostCentersAsync(credentials, cancellationToken),
            SiigoOptionLists.Warehouses => await api.ListWarehousesAsync(credentials, cancellationToken),
            _ => await api.ListPaymentTypesAsync(credentials, cancellationToken),
        };
        return [.. options
            .OrderBy(option => option.Name, StringComparer.InvariantCultureIgnoreCase)
            .Select(option => new SiigoOptionDto(option.Id, option.Name, null, null, option.Description))];
    }
}
