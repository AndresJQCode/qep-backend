using System.Globalization;
using BuildingBlocks.Domain.Pricing;

namespace Modules.ServiceContracts.Domain;

/// <summary>La foto de la revisión de un contrato en una fecha. Fuera de <c>Active</c> todo va nulo y el estado es <c>None</c>.</summary>
/// <param name="ScheduleAppliesTo">El período al que aplicará el cambio programado pendiente, cubra o no la revisión.</param>
public sealed record PriceReviewSnapshot(
    DateOnly? ReviewPeriodDate,
    DateOnly? GenerationDate,
    DateOnly? NoticeFrom,
    ReviewState State,
    int? IpcYear,
    DateOnly? ScheduleAppliesTo);

/// <summary>
/// La regla de la revisión de precio (spec reajuste §5, §6.1 y §9), pura: la usan los DTO, las acciones, la lista y el digest,
/// así que el período, las fechas y el estado no pueden discrepar entre ellos.
/// </summary>
public static class PriceReview
{
    /// <summary>Con cuántos días de anticipación al período empieza el aviso: 10 antes de que se genere el cobro.</summary>
    public const int NoticeDays = 15;

    // R-8: el motivo lo lee una persona en el historial, con coma decimal. Armado a mano para no depender de los datos de cultura.
    private static readonly NumberFormatInfo CommaDecimal = NumberFormatInfo.ReadOnly(new NumberFormatInfo { NumberDecimalSeparator = "," });

    public static PriceReviewSnapshot Of(ServiceContract service, ServicePriceSchedule? pending, DateOnly today)
    {
        if (service.Status != ServiceContractStatus.Active)
        {
            return new PriceReviewSnapshot(null, null, null, ReviewState.None, null, null);
        }

        var period = ReviewPeriodOf(service);
        var noticeFrom = period.AddDays(-NoticeDays);
        DateOnly? appliesTo = pending is null ? null : AppliesTo(service, pending.EffectiveFrom);
        var state = service.HeldSince is not null ? ReviewState.Held
            : appliesTo == period ? ReviewState.Scheduled
            : today >= noticeFrom ? ReviewState.Due
            : ReviewState.Upcoming;

        return new PriceReviewSnapshot(
            period,
            period.AddDays(-ChargeGenerator.WindowDays),
            noticeFrom,
            state,
            // P-2: el IPC del año calendario anterior a la revisión.
            service.AdjustmentMode == AdjustmentMode.Ipc ? period.Year - 1 : null,
            appliesTo);
    }

    /// <summary>El primer período sin generar <c>&gt;= NextReviewDate</c>, respetando el salto de la pausa.</summary>
    public static DateOnly ReviewPeriodOf(ServiceContract service) => AppliesTo(service, service.NextReviewDate);

    /// <summary>El primer período sin generar <c>&gt;= effectiveFrom</c>.</summary>
    public static DateOnly AppliesTo(ServiceContract service, DateOnly effectiveFrom) =>
        service.FirstUngeneratedPeriodOnOrAfter(effectiveFrom);

    /// <summary>Un cambio programado cubre la revisión si aplica exactamente en el período de revisión.</summary>
    public static bool Covers(ServiceContract service, ServicePriceSchedule pending) =>
        AppliesTo(service, pending.EffectiveFrom) == ReviewPeriodOf(service);

    /// <summary>La invariante de <c>Ipc</c> (spec reajuste §5): un pendiente nunca aplica después del período de revisión.</summary>
    public static void EnsureNotAfterReview(ServiceContract service, ServicePriceSchedule? pending)
    {
        if (service.AdjustmentMode == AdjustmentMode.Ipc
            && pending is not null
            && AppliesTo(service, pending.EffectiveFrom) > ReviewPeriodOf(service))
        {
            throw new ServiceContractsDomainException(
                ServiceContractsErrorCodes.PriceScheduleAfterReview,
                "An IPC service cannot have a price change after its review period.");
        }
    }

    /// <summary>El precio calculado con el IPC, redondeado con la regla de la moneda; nulo si no queda por encima de cero.</summary>
    public static decimal? IpcPrice(decimal price, decimal ipcRate, decimal extraPoints, PriceRounding rounding)
    {
        var rounded = ServiceContract.ToMoney(rounding.Apply(price * (1 + ((ipcRate + extraPoints) / 100))));
        return rounded > 0 ? rounded : null;
    }

    /// <summary>El motivo automático: <c>IPC 2026 (5,20 %) + 2,00 puntos</c>, sin los puntos cuando son 0.</summary>
    public static string IpcReason(int year, decimal rate, decimal extraPoints)
    {
        var reason = string.Create(CultureInfo.InvariantCulture, $"IPC {year} ({rate.ToString("0.00", CommaDecimal)} %)");
        return extraPoints == 0
            ? reason
            : $"{reason} + {extraPoints.ToString("0.00", CommaDecimal)} puntos";
    }
}
