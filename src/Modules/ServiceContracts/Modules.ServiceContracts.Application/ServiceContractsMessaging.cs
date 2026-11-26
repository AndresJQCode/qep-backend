namespace Modules.ServiceContracts.Application;

/// <summary>Lo que un puerto de envío le devuelve al worker: <see cref="Sent"/> o <see cref="Failed"/> con el <c>failure_code</c> de §3.4.</summary>
public abstract record ServiceContractsSendResult
{
    private ServiceContractsSendResult()
    {
    }

    public sealed record Sent : ServiceContractsSendResult;

    public sealed record Failed(string Code) : ServiceContractsSendResult;
}

public enum ServiceContractsMessageKind
{
    Reminder = 1,
    Overdue = 2,
}

/// <summary>
/// Cómo se escriben fechas y números en el email. Es el espejo de <c>EmailRegionalFormat</c> de Notifications.Application, que
/// ServiceContracts no puede referenciar: el adaptador de Bootstrapper la traduce.
/// </summary>
public sealed record ServiceContractsEmailFormat(string TimeZoneId, string DateFormat, string NumberFormat);

public sealed record ServiceContractsMessageLine(string ServiceName, decimal Amount, string Currency, DateOnly DueDate, int DaysOverdue);

/// <summary>El mensaje del día a un cliente (§3.3, paso 4). <c>ToAddress</c> es el correo o el <c>wa_id</c>, según el canal.</summary>
public sealed record ServiceContractsCustomerMessage(
    ServiceContractsMessageKind Kind,
    string ToAddress,
    string CustomerName,
    string TenantName,
    IReadOnlyList<ServiceContractsMessageLine> Lines,
    IReadOnlyList<CurrencyAmountDto> TotalByCurrency,
    DateOnly NearestDueDate,
    ServiceContractsEmailFormat Format);

/// <summary><c>FailureCode</c>: el fallo del último aviso del cobro, o null si no falló.</summary>
public sealed record ServiceContractsDigestRow(
    string CustomerName,
    string ServiceName,
    decimal Amount,
    string Currency,
    DateOnly DueDate,
    string? FailureCode);

/// <summary>
/// Una revisión de precio abierta en el email diario (spec reajuste §6.6). <c>State</c> es el nombre del estado (<c>Due</c> o
/// <c>Held</c>); <c>IpcYear</c> es nulo en <c>Manual</c>; <c>IpcMissing</c> dice que ese año todavía no tiene tasa cargada.
/// </summary>
public sealed record ServiceContractsPriceReviewRow(
    string CustomerName,
    string ServiceName,
    decimal CurrentPrice,
    string Currency,
    DateOnly ReviewPeriodDate,
    DateOnly GenerationDate,
    string State,
    int? IpcYear,
    bool IpcMissing,
    DateOnly? HeldSince);

/// <summary>
/// El email diario al encargado (§3.3, paso 5). <c>PriceReviews</c> (por revisar) y <c>HeldCharges</c> (retenidos) vienen
/// ordenadas por fecha de generación (spec reajuste §6.6).
/// </summary>
public sealed record ServiceContractsDigestEmail(
    string ToAddress,
    string MemberName,
    string TenantName,
    IReadOnlyList<ServiceContractsDigestRow> DueSoon,
    IReadOnlyList<ServiceContractsDigestRow> Overdue,
    IReadOnlyList<ServiceContractsDigestRow> FailedReminders,
    IReadOnlyList<CurrencyAmountDto> OverdueTotalByCurrency,
    ServiceContractsEmailFormat Format,
    IReadOnlyList<ServiceContractsPriceReviewRow> PriceReviews,
    IReadOnlyList<ServiceContractsPriceReviewRow> HeldCharges);
