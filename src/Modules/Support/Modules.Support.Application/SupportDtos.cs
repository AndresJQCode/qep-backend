using Modules.Support.Domain;

namespace Modules.Support.Application;

// Contrato para las pantallas de qep-frontend (plan, «Contrato para las pantallas»). Los enums viajan por nombre: el diccionario
// de textos lo tiene el frontend.

/// <summary>Un tipo de ticket como lo dibujan el selector del alta y la tabla de ajustes. <c>Version</c> va en el <c>If-Match</c> del
/// <c>PUT</c>; <c>Position</c> es el orden en que la pantalla los lista.</summary>
public sealed record SupportTicketTypeDto(
    Guid Id,
    string Name,
    int? TermBusinessDays,
    bool TermIsLegal,
    bool RequiresFormalResponse,
    bool IsPublished,
    bool IsActive,
    int Position,
    long Version);

/// <summary>El umbral de «por vencer» del tenant [owner 2026-11-14]. Viaja con su rango (<c>Min</c> y <c>Max</c>) para que el
/// formulario de ajustes no lo repita; 0 significa «apagado». Un tenant que nunca guardó ve 3.</summary>
public sealed record SupportSettingsDto(int DueSoonBusinessDays, int MinDueSoonBusinessDays, int MaxDueSoonBusinessDays, long Version);

internal static class SupportDtoMapping
{
    public static SupportTicketTypeDto ToDto(SupportTicketType type) => new(
        type.Id.Value,
        type.Name,
        type.TermBusinessDays,
        type.TermIsLegal,
        type.RequiresFormalResponse,
        type.IsPublished,
        type.IsActive,
        type.Position,
        type.Version);

    public static SupportSettingsDto ToDto(SupportTenantSettings settings) => new(
        settings.DueSoonBusinessDays,
        SupportTenantSettings.MinDueSoonBusinessDays,
        SupportTenantSettings.MaxDueSoonBusinessDays,
        settings.Version);
}
