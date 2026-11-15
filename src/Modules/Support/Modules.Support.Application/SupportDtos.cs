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

/// <summary>Una página de la bandeja. <c>Summary</c> es del tenant entero, no de la página ni del filtro: las pestañas cuentan siempre
/// lo mismo, filtres lo que filtres.</summary>
public sealed record SupportTicketPageDto(
    IReadOnlyList<SupportTicketListItemDto> Items, int Total, int Page, int PageSize, SupportInboxSummaryDto Summary);

/// <summary><c>ByStatus</c> trae los seis estados, en el orden de la máquina, también en cero: las pestañas no conocen el enum.</summary>
public sealed record SupportInboxSummaryDto(IReadOnlyList<SupportStatusCountDto> ByStatus, int Overdue, int DueSoon);

public sealed record SupportStatusCountDto(string Status, int Count);

/// <summary>Una fila de la bandeja. <c>RequesterDisplayName</c> lo resuelve el backend (cliente vinculado; si no, nombres y
/// apellidos; si no, razón social; si no, «NIT número») para que la fila no repita la regla. <c>DueState</c> y
/// <c>BusinessDaysLeft</c> ya vienen evaluados con el calendario y el umbral del tenant.</summary>
public sealed record SupportTicketListItemDto(
    Guid Id,
    string Number,
    string Subject,
    string Status,
    string TypeName,
    string RequesterDisplayName,
    SupportMemberDto? Assignee,
    DateTimeOffset ReceivedAt,
    DateOnly? DueOn,
    string DueState,
    int? BusinessDaysLeft);

/// <summary>El detalle del ticket. <c>AllowedTransitions</c> sale de la máquina de estados del dominio: el selector de estado no la
/// repite. <c>Version</c> va en el <c>If-Match</c> de las acciones.</summary>
public sealed record SupportTicketDetailDto(
    Guid Id,
    string Number,
    string Subject,
    string Description,
    string Status,
    IReadOnlyList<string> AllowedTransitions,
    SupportTicketTypeSnapshotDto Type,
    SupportClockDto Clock,
    string Intake,
    string? ManualMedium,
    string ReplyChannel,
    SupportCustomerRefDto? Customer,
    SupportRequesterDto? Requester,
    SupportMemberDto? Assignee,
    int ResolutionCount,
    DateTimeOffset ReceivedAt,
    IReadOnlyList<SupportTicketEventDto> Events,
    long Version);

/// <summary>La foto del tipo que guardó el ticket: editar el tipo después no la cambia.</summary>
public sealed record SupportTicketTypeSnapshotDto(Guid Id, string Name, int? TermBusinessDays, bool TermIsLegal, bool RequiresFormalResponse);

/// <summary><c>DueState</c> ∈ <c>NoTerm</c>, <c>OnTrack</c>, <c>DueSoon</c>, <c>Overdue</c>, <c>Paused</c>, <c>Finished</c>.
/// <c>TermIsLegal</c> decide si la pantalla dice «vence el plazo legal» o «vence el objetivo».</summary>
public sealed record SupportClockDto(
    DateOnly? DueOn, string DueState, int? BusinessDaysLeft, int? TermBusinessDays, bool TermIsLegal, int PausedBusinessDays);

/// <summary>Una entrada de la traza, con campos planos y nulos según <c>Kind</c> y los nombres ya resueltos: la pantalla no lee
/// JSON ni busca miembros. Un miembro o cliente que ya no se encuentra sale como «Sin nombre».</summary>
public sealed record SupportTicketEventDto(
    int Ordinal,
    string Kind,
    DateTimeOffset OccurredAt,
    SupportMemberDto? Actor,
    string? FromStatus,
    string? ToStatus,
    SupportMemberDto? Assignee,
    string? FromTypeName,
    string? ToTypeName,
    SupportCustomerRefDto? Customer);

public sealed record SupportMemberDto(Guid Id, string DisplayName);

/// <summary><c>IsActive</c> falso: el cliente se desactivó o se fusionó; la pantalla lo marca.</summary>
public sealed record SupportCustomerRefDto(Guid Id, string Name, bool IsActive);

/// <summary>La foto del solicitante tomada al radicar (plan D7).</summary>
public sealed record SupportRequesterDto(
    string? FirstNames,
    string? LastNames,
    string? BusinessName,
    string? DocumentType,
    string? DocumentNumber,
    string? Email,
    string? MobilePhone,
    string? LandlinePhone,
    string CountryCode,
    string? Address);

/// <summary>Los candidatos a responsable: exactamente los que el backend acepta al asignar.</summary>
public sealed record SupportAssigneesDto(IReadOnlyList<SupportMemberDto> Items);

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
