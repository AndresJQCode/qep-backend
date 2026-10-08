using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>
/// Spec 2026-10-08 §5: la lectura de la consola de operador, a través de todos los tenants. Los registros
/// son del lado de lectura y llevan tipos de dominio, sin texto: el texto del contrato lo pone
/// <see cref="OperatorTenantDetails"/> y cada handler.
/// </summary>
public interface IOperatorTenantReader
{
    /// <summary>Una página ordenada por nombre y luego id; <paramref name="search"/> en nombre o slug, con
    /// los comodines tratados como literales. El resumen cuenta todos los tenants, sin la búsqueda.</summary>
    Task<OperatorTenantListing> ListAsync(string? search, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>null si el tenant no existe.</summary>
    Task<OperatorTenantSnapshot?> FindAsync(TenantId tenantId, CancellationToken cancellationToken);
}

/// <param name="Total">Los que cumplen la búsqueda, no sólo los de la página.</param>
/// <param name="AllTenants">Todos, sin la búsqueda.</param>
/// <param name="WithoutModules">Tenants sin ninguna fila activa (decisión P7 del plan).</param>
/// <param name="Inactive">Tenants cuyo estado no es <see cref="TenantStatus.Active"/>.</param>
public sealed record OperatorTenantListing(
    IReadOnlyList<OperatorTenantRow> Rows, int Total, int AllTenants, int WithoutModules, int Inactive);

/// <param name="ActiveModules">Filas con estado activo (contratados), no efectivos.</param>
public sealed record OperatorTenantRow(
    Guid TenantId, string Slug, string DisplayName, TenantStatus Status, DateTimeOffset CreatedAt, int ActiveModules);

/// <param name="Modules">Sólo los que tienen fila.</param>
/// <param name="LastModuleReasons">El motivo del último cambio de cada módulo en el historial.</param>
/// <param name="LastStatusChange">El último cambio de estado del tenant en el historial, o null.</param>
public sealed record OperatorTenantSnapshot(
    Guid TenantId,
    string Slug,
    string DisplayName,
    DateTimeOffset CreatedAt,
    TenantStatus Status,
    long Version,
    IReadOnlyList<TenantModuleState> Modules,
    IReadOnlyDictionary<TenantModuleKey, ChangeReason> LastModuleReasons,
    TenantStatusChange? LastStatusChange);

public sealed record TenantModuleState(
    TenantModuleKey Key, TenantModuleStatus Status, DateTimeOffset StatusChangedAt, string Source);

public sealed record TenantStatusChange(DateTimeOffset OccurredAt, ChangeReason Reason);
