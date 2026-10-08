namespace Modules.Tenancy.Application;

/// <summary>
/// Una página de tenants para la consola. <see cref="Summary"/> viaja con la página por la misma razón que
/// <c>/reports/orders/summary</c>: sumarlo en el cliente depende de la página que se mire; cuenta todos
/// los tenants, sin el filtro de búsqueda.
/// </summary>
public sealed record OperatorTenantPageDto(
    IReadOnlyList<OperatorTenantListItemDto> Items, int Total, int Page, int PageSize, OperatorTenantSummaryDto Summary);

/// <param name="Status">El nombre del enum (<c>Active</c>, <c>Suspended</c>): el diccionario lo tiene la SPA.</param>
/// <param name="ActiveModules">Filas activas (contratados), no efectivos (decisión P7 del plan).</param>
/// <param name="TotalModules"><c>TenantModuleKeys.All.Count</c>: la pantalla no conoce la lista del backend
/// para dibujar «n de 7».</param>
/// <param name="IsOperator">Marca al operador sin que la SPA conozca la configuración.</param>
public sealed record OperatorTenantListItemDto(
    Guid TenantId, string Slug, string DisplayName, string Status, DateTimeOffset CreatedAt,
    int ActiveModules, int TotalModules, bool IsOperator);

public sealed record OperatorTenantSummaryDto(int Total, int WithoutModules, int Inactive);

/// <param name="StatusChangedAt">Del último cambio de estado del tenant en el historial; null si nunca cambió.</param>
/// <param name="Version">La del agregado: es el <c>If-Match</c> de <c>POST …/status</c>.</param>
/// <param name="Modules">Siempre las siete, en el orden de <c>TenantModuleKeys.All</c>, aunque no tengan fila:
/// si faltara una, la SPA tendría que conocer la lista del backend para dibujarla.</param>
public sealed record OperatorTenantDetailDto(
    Guid TenantId, string Slug, string DisplayName, DateTimeOffset CreatedAt, string Status,
    DateTimeOffset? StatusChangedAt, string? StatusReason, long Version, bool IsOperator,
    IReadOnlyList<OperatorTenantModuleDto> Modules);

/// <param name="Status"><c>active</c>, <c>inactive</c> o <c>none</c> (sin fila): «nunca se activó» y «se
/// desactivó» se dibujan distinto.</param>
/// <param name="Enabled">El efectivo, con las dependencias cerradas.</param>
/// <param name="Dependencies">Directas: la consola calcula la cascada sin duplicar el grafo del backend.</param>
/// <param name="Since"><c>status_changed_at</c>; null sin fila.</param>
/// <param name="LastReason">Del último cambio de este módulo en el historial; null si nunca lo tocó la consola.</param>
public sealed record OperatorTenantModuleDto(
    string Key, string Status, bool Enabled, IReadOnlyList<string> Dependencies,
    DateTimeOffset? Since, string? Source, string? LastReason)
{
    public const string NoRowStatus = "none";
}

/// <summary>Lotes del más reciente al más antiguo, paginados <b>por lote</b> y no por fila: la cascada de
/// una operación se dibuja junta. El historial es inmutable: no hay endpoint para editarlo ni borrarlo.</summary>
public sealed record OperatorHistoryPageDto(IReadOnlyList<OperatorHistoryBatchDto> Items, int Total, int Page, int PageSize);

/// <param name="Kind"><c>module</c> o <c>tenant_status</c>, el mismo texto que la columna.</param>
/// <param name="ActorEmail">Resuelto por Identity; null si el usuario ya no existe y la UI muestra el id corto.</param>
public sealed record OperatorHistoryBatchDto(
    Guid BatchId, string Kind, DateTimeOffset OccurredAt, Guid ActorUserId, string? ActorEmail, string Reason,
    string? Note, IReadOnlyList<OperatorHistoryChangeDto> Changes);

/// <param name="ModuleKey">null en un cambio de estado del tenant.</param>
/// <param name="FromStatus">null = la fila del módulo no existía.</param>
public sealed record OperatorHistoryChangeDto(string? ModuleKey, string? FromStatus, string ToStatus);
