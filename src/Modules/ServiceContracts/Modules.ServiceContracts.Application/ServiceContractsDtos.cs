namespace Modules.ServiceContracts.Application;

// Contrato HTTP de cobros-recurrentes §4. Convención: los opcionales viajan como null, nunca ausentes;
// los enums van por el cable como texto (Pending | Overdue | Paid | Cancelled, Monthly | Semiannual | Annual,
// WhatsApp | Email). Un ChargeDto con status Overdue es un cobro Pending vencido: Overdue no se guarda.

public sealed record MemberRefDto(Guid MemberId, string DisplayName, bool IsActive);

public sealed record CustomerRefDto(Guid Id, string Name, string? PhoneE164, string? Email);

public sealed record CurrencyAmountDto(string Currency, decimal Amount);

public sealed record ProfileDto(Guid CustomerId, string Channel, MemberRefDto Assignee, long Version);

/// <summary>El último mensaje que cubrió algún paso del cobro; <c>Kind</c> es Reminder u Overdue.</summary>
public sealed record ReminderDto(
    string Step,
    string Kind,
    string Channel,
    string Status,
    string? FailureCode,
    DateTimeOffset AttemptedAt,
    DateTimeOffset? SentAt);

public sealed record PaymentDto(DateOnly PaidOn, string? Note, MemberRefDto? PaidBy, DateTimeOffset PaidAt);

public sealed record ChargeDto(
    Guid Id,
    Guid ServiceId,
    string ServiceName,
    CustomerRefDto Customer,
    MemberRefDto? Assignee,
    decimal Amount,
    string Currency,
    DateOnly DueDate,
    string Status,
    int DaysOverdue,
    PaymentDto? Payment,
    ReminderDto? LastReminder,
    long Version);

public sealed record ServiceDto(
    Guid Id,
    CustomerRefDto Customer,
    string Name,
    string? Description,
    decimal Price,
    string Currency,
    string Cycle,
    DateOnly FirstDueDate,
    string Status,
    ChargeDto? NextCharge,
    int OpenCount,
    IReadOnlyList<CurrencyAmountDto> OpenAmountByCurrency,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Una fila del historial de precios del contrato: de qué precio y moneda a cuáles, el motivo obligatorio y quién lo cambió
/// (<c>null</c> si quien editó no tenía membresía activa, como <c>PaymentDto.PaidBy</c>). Viaja dentro del detalle y no en un
/// endpoint propio: la ficha lo pinta debajo del encabezado y son pocas filas (un precio cambia unas veces al año), así que una
/// segunda llamada sólo le costaría una carga más a la pantalla.
/// </summary>
public sealed record ServicePriceChangeDto(
    Guid Id,
    decimal PreviousPrice,
    string PreviousCurrency,
    decimal NewPrice,
    string NewCurrency,
    string Reason,
    MemberRefDto? ChangedBy,
    DateTimeOffset OccurredAt);

/// <param name="PriceChanges">Del más reciente al más viejo; vacío si el precio nunca cambió.</param>
public sealed record ServiceDetailDto(
    ServiceDto Service,
    ProfileDto Profile,
    IReadOnlyList<ChargeDto> Charges,
    IReadOnlyList<ServicePriceChangeDto> PriceChanges);

/// <summary>Deuda acumulada del tenant entero (status=open, sin los demás filtros), un total por moneda sin convertir.</summary>
public sealed record ChargeSummaryDto(
    int OpenCount,
    int OverdueCount,
    int FailedReminderCount,
    IReadOnlyList<CurrencyAmountDto> OpenAmountByCurrency,
    IReadOnlyList<CurrencyAmountDto> OverdueAmountByCurrency);

public sealed record ChargePageDto(
    IReadOnlyList<ChargeDto> Items,
    int Total,
    int Page,
    int PageSize,
    ChargeSummaryDto Summary);

public sealed record ServicePageDto(IReadOnlyList<ServiceDto> Items, int Total, int Page, int PageSize);

/// <summary>Lo que <c>POST /services</c> crearía hoy con esos datos, sin escribir: el aviso «Se van a crear N cobros vencidos» del formulario.</summary>
public sealed record ChargesPreviewDto(int ChargesToGenerate, int OverdueCharges);

public sealed record AssigneesDto(IReadOnlyList<MemberRefDto> Items);

/// <summary><c>NotReadyReason</c>: <c>template_not_approved</c> | <c>connection_unavailable</c> | null.</summary>
public sealed record ServiceContractsConnectionDto(Guid Id, string Name, string? Phone, bool Ready, string? NotReadyReason);

public sealed record ServiceContractsSettingsDto(Guid? WhatsAppConnectionId, IReadOnlyList<ServiceContractsConnectionDto> Connections);
