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
/// para dibujar «n de N».</param>
/// <param name="IsOperator">Marca al operador sin que la SPA conozca la configuración.</param>
public sealed record OperatorTenantListItemDto(
    Guid TenantId, string Slug, string DisplayName, string Status, DateTimeOffset CreatedAt,
    int ActiveModules, int TotalModules, bool IsOperator);

public sealed record OperatorTenantSummaryDto(int Total, int WithoutModules, int Inactive);

/// <param name="StatusChangedAt">Del último cambio de estado del tenant en el historial; null si nunca cambió.</param>
/// <param name="Version">La del agregado: es el <c>If-Match</c> de <c>POST …/status</c>.</param>
/// <param name="Modules">Siempre todas, en el orden de <c>TenantModuleKeys.All</c>, aunque no tengan fila:
/// si faltara una, la SPA tendría que conocer la lista del backend para dibujarla.</param>
/// <param name="Providers">Siempre todos, en el orden de <c>TenantProviderKeys.All</c>, aunque no tengan fila
/// (plan 2026-10-21): mismo motivo que <paramref name="Modules"/>; un tenant nuevo no tiene ninguna.</param>
public sealed record OperatorTenantDetailDto(
    Guid TenantId, string Slug, string DisplayName, DateTimeOffset CreatedAt, string Status,
    DateTimeOffset? StatusChangedAt, string? StatusReason, long Version, bool IsOperator,
    IReadOnlyList<OperatorTenantModuleDto> Modules, IReadOnlyList<OperatorTenantProviderDto> Providers);

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

/// <param name="Status"><c>active</c>, <c>inactive</c> o <c>none</c> (sin fila,
/// <see cref="OperatorTenantModuleDto.NoRowStatus"/>): «nunca se habilitó» y «se deshabilitó» se dibujan
/// distinto. Sin <c>Enabled</c> ni dependencias: un proveedor no depende de nada, así que el estado ya es el
/// efectivo.</param>
/// <param name="Since"><c>status_changed_at</c>; null sin fila.</param>
/// <param name="LastReason">Del último cambio de este proveedor en el historial; null si nunca lo tocó la
/// consola (el backfill de la migración no escribe historial).</param>
public sealed record OperatorTenantProviderDto(string Key, string Status, DateTimeOffset? Since, string? LastReason);

/// <summary>Lotes del más reciente al más antiguo, paginados <b>por lote</b> y no por fila: la cascada de
/// una operación se dibuja junta. El historial es inmutable: no hay endpoint para editarlo ni borrarlo.</summary>
public sealed record OperatorHistoryPageDto(IReadOnlyList<OperatorHistoryBatchDto> Items, int Total, int Page, int PageSize);

/// <param name="Kind"><c>module</c>, <c>tenant_status</c> o <c>provider</c>, el mismo texto que la columna.</param>
/// <param name="ActorEmail">Resuelto por Identity; null si el usuario ya no existe y la UI muestra el id corto.</param>
public sealed record OperatorHistoryBatchDto(
    Guid BatchId, string Kind, DateTimeOffset OccurredAt, Guid ActorUserId, string? ActorEmail, string Reason,
    string? Note, IReadOnlyList<OperatorHistoryChangeDto> Changes);

/// <param name="ModuleKey">null en un cambio de estado del tenant o de proveedor.</param>
/// <param name="FromStatus">null = la fila del módulo o del proveedor no existía.</param>
/// <param name="ProviderKey">Sólo en un cambio de proveedor. Al final, para no mover el orden posicional del
/// resto.</param>
public sealed record OperatorHistoryChangeDto(string? ModuleKey, string? FromStatus, string ToStatus, string? ProviderKey);
