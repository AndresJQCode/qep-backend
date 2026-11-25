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

/// <summary>
/// El cambio de precio pendiente de un contrato. Viaja completo, con su autor y con el período en que va a aplicar
/// (<c>AppliesToPeriodDate</c>), porque la pantalla no cuenta anclas del ciclo: «desde» puede caer entre dos períodos, o en una
/// pausa, y sólo el backend sabe cuál lo alcanza primero.
/// </summary>
public sealed record PriceScheduleDto(
    Guid Id,
    DateOnly EffectiveFrom,
    DateOnly AppliesToPeriodDate,
    decimal NewPrice,
    string Currency,
    string Source,
    int? IpcYear,
    decimal? IpcRate,
    decimal? ExtraPoints,
    string Reason,
    MemberRefDto? CreatedBy,
    DateTimeOffset CreatedAt);

/// <summary>
/// El reajuste de un contrato y la foto de su revisión hoy (spec reajuste §6.1). El período, las fechas, el estado
/// (<c>None | Upcoming | Due | Held | Scheduled</c>) y el año de IPC los calcula el backend con una sola regla: la pantalla sólo
/// los pinta. Fuera de <c>Active</c> las fechas y el año van nulos y el estado es <c>None</c>.
/// </summary>
public sealed record PriceReviewInfoDto(
    string AdjustmentMode,
    decimal IpcExtraPoints,
    DateOnly NextReviewDate,
    DateOnly? ReviewPeriodDate,
    DateOnly? GenerationDate,
    DateOnly? NoticeFrom,
    string State,
    DateOnly? HeldSince,
    int? IpcYear,
    PriceScheduleDto? Scheduled);

/// <summary>
/// Cuántos contratos del tenant entero esperan una decisión. Viajan los dos conteos siempre, también en cero: la bandeja de
/// cobros dibuja su aviso sin pedir la lista de revisiones ni sumar páginas.
/// </summary>
public sealed record PriceReviewCountsDto(int DueCount, int HeldCount)
{
    public static PriceReviewCountsDto Empty { get; } = new(0, 0);
}

/// <summary>
/// <c>PeriodDate</c> es el período que el cobro paga y <c>DueDate</c> lo que el cliente ve: difieren en un cobro que estuvo
/// retenido. <c>PriceUnreviewed</c> marca el que salió con el precio viejo porque nadie revisó a tiempo.
/// </summary>
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
    long Version,
    DateOnly PeriodDate,
    bool PriceUnreviewed);

/// <summary><c>PriceReview</c> va en cada contrato, también en la lista, para marcar los que esperan revisión sin otra consulta.</summary>
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
    DateTimeOffset UpdatedAt,
    PriceReviewInfoDto PriceReview);

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
    DateTimeOffset OccurredAt,
    string Origin);

/// <param name="PriceChanges">Del más reciente al más viejo; vacío si el precio nunca cambió.</param>
/// <param name="UpcomingPeriodDates">
/// Desde cuáles períodos se puede programar un cambio: hasta 12 sin generar; vacío si el contrato no está <c>Active</c> y, en
/// <c>Ipc</c>, sólo hasta el de la revisión. Viaja calculado porque el selector no puede conocer las anclas ni la invariante.
/// </param>
public sealed record ServiceDetailDto(
    ServiceDto Service,
    ProfileDto Profile,
    IReadOnlyList<ChargeDto> Charges,
    IReadOnlyList<ServicePriceChangeDto> PriceChanges,
    IReadOnlyList<DateOnly> UpcomingPeriodDates);

/// <summary>Deuda acumulada del tenant entero (status=open, sin los demás filtros), un total por moneda sin convertir.</summary>
public sealed record ChargeSummaryDto(
    int OpenCount,
    int OverdueCount,
    int FailedReminderCount,
    IReadOnlyList<CurrencyAmountDto> OpenAmountByCurrency,
    IReadOnlyList<CurrencyAmountDto> OverdueAmountByCurrency,
    PriceReviewCountsDto PriceReviews);

public sealed record ChargePageDto(
    IReadOnlyList<ChargeDto> Items,
    int Total,
    int Page,
    int PageSize,
    ChargeSummaryDto Summary);

/// <summary>
/// La franja de la lista de contratos: el tenant entero, sin los filtros ni la página, un total por moneda sin convertir.
/// Lo vencido es el mismo cálculo de <see cref="ChargeSummaryDto"/>, para que esta pantalla y la bandeja de cobros no puedan
/// discrepar. <c>MonthlyValueByCurrency</c> cuenta sólo los contratos <c>Active</c>, prorrateados por mes.
/// </summary>
public sealed record ServiceSummaryDto(
    int ActiveCount,
    int OverdueCount,
    IReadOnlyList<CurrencyAmountDto> OverdueAmountByCurrency,
    IReadOnlyList<CurrencyAmountDto> MonthlyValueByCurrency);

public sealed record ServicePageDto(
    IReadOnlyList<ServiceDto> Items,
    int Total,
    int Page,
    int PageSize,
    ServiceSummaryDto Summary);

/// <summary>
/// Lo que <c>POST /services</c> crearía hoy con esos datos, sin escribir: el aviso «Se van a crear N cobros vencidos» del
/// formulario. <c>DefaultNextReviewDate</c> es la revisión que el alta pondría si no se escribe otra: viaja para que el
/// formulario la proponga sin contar anclas del ciclo en el cliente.
/// </summary>
public sealed record ChargesPreviewDto(int ChargesToGenerate, int OverdueCharges, DateOnly DefaultNextReviewDate);

public sealed record AssigneesDto(IReadOnlyList<MemberRefDto> Items);

/// <summary><c>NotReadyReason</c>: <c>template_not_approved</c> | <c>connection_unavailable</c> | null.</summary>
public sealed record ServiceContractsConnectionDto(Guid Id, string Name, string? Phone, bool Ready, string? NotReadyReason);

public sealed record ServiceContractsSettingsDto(Guid? WhatsAppConnectionId, IReadOnlyList<ServiceContractsConnectionDto> Connections);
