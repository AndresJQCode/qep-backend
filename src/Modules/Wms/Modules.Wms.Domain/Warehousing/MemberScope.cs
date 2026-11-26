namespace Modules.Wms.Domain.Warehousing;

/// <summary>
/// Alcance de un miembro sobre un dueño o una bodega. Entidad sin comportamiento: existe sólo para que <c>wms.member_scopes</c>
/// nazca en <c>InitialWms</c> (S18, D-07). Nace vacía y nadie la lee en este corte; la fase 3 le pone reglas y, como está
/// vacía, puede cambiarle claves por migración.
/// </summary>
public sealed class MemberScope
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid MemberId { get; init; }

    public Guid? OwnerId { get; init; }

    public Guid? WarehouseId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
