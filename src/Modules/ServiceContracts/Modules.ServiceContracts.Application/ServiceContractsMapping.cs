using Modules.ServiceContracts.Domain;
using Modules.Tenancy.Application;

namespace Modules.ServiceContracts.Application;

public static class ServiceContractsMapping
{
    /// <summary>
    /// <c>Status</c> es <c>Overdue</c> cuando el cobro está <c>Pending</c> y venció antes de <paramref name="today"/> (la «hoy»
    /// del tenant, no la del servidor); <c>DaysOverdue</c> es 0 si no vence.
    /// </summary>
    public static ChargeDto ToChargeDto(
        Charge charge,
        ServiceContract service,
        CustomerRefDto customer,
        MemberRefDto? assignee,
        MemberRefDto? paidBy,
        ReminderDto? last,
        DateOnly today) => new(
            charge.Id,
            service.Id.Value,
            service.Name,
            customer,
            assignee,
            charge.Amount,
            charge.Currency,
            charge.DueDate,
            charge.IsOverdue(today) ? "Overdue" : charge.Status.ToString(),
            // Sólo lo vencido cuenta días: un cobro pagado o cancelado con vencimiento pasado no está «vencido hace N días».
            charge.IsOverdue(today) ? today.DayNumber - charge.DueDate.DayNumber : 0,
            charge.PaidOn is { } paidOn
                ? new PaymentDto(paidOn, charge.PaymentNote, paidBy, charge.PaidAt ?? default)
                : null,
            last,
            charge.Version,
            charge.PeriodDate,
            charge.PriceUnreviewed);

    /// <summary>
    /// El reajuste de un contrato con la foto de su revisión. <paramref name="pending"/> es su cambio programado pendiente y
    /// <paramref name="createdBy"/> quien lo programó. En un pausado la foto no dice a qué período aplicará, así que se calcula
    /// desde el primer período sin generar, que es por donde retoma.
    /// </summary>
    public static PriceReviewInfoDto ToPriceReviewDto(
        ServiceContract service,
        ServicePriceSchedule? pending,
        PriceReviewSnapshot review,
        MemberRefDto? createdBy) => new(
            service.AdjustmentMode.ToString(),
            service.IpcExtraPoints,
            service.NextReviewDate,
            review.ReviewPeriodDate,
            review.GenerationDate,
            review.NoticeFrom,
            review.State.ToString(),
            service.HeldSince,
            review.IpcYear,
            pending is null
                ? null
                : new PriceScheduleDto(
                    pending.Id,
                    pending.EffectiveFrom,
                    review.ScheduleAppliesTo ?? PriceReview.AppliesTo(service, pending.EffectiveFrom),
                    pending.NewPrice,
                    pending.Currency,
                    pending.Source.ToString(),
                    pending.IpcYear,
                    pending.IpcRate,
                    pending.ExtraPoints,
                    pending.Reason,
                    createdBy,
                    pending.CreatedAt));

    public static ServiceDto ToServiceDto(
        ServiceContract service,
        CustomerRefDto customer,
        ChargeDto? nextCharge,
        int openCount,
        IReadOnlyList<CurrencyAmountDto> openAmountByCurrency,
        PriceReviewInfoDto priceReview) => new(
            service.Id.Value,
            customer,
            service.Name,
            service.Description,
            service.Price,
            service.Currency,
            service.Cycle.ToString(),
            service.FirstDueDate,
            service.Status.ToString(),
            nextCharge,
            openCount,
            openAmountByCurrency,
            service.Version,
            service.CreatedAt,
            service.UpdatedAt,
            priceReview);

    /// <summary>
    /// Una membresía del equipo como la ve el cliente de la API. Un id que el directorio ya no resuelve (la fila se purgó) se
    /// muestra inactivo y sin nombre, como el directorio llama a un miembro sin nombre, en vez de perder la referencia.
    /// </summary>
    public static MemberRefDto ToMemberRefDto(Guid memberId, ServiceContractsMemberRef? member) =>
        member is null
            ? new MemberRefDto(memberId, "Sin nombre", false)
            : new MemberRefDto(member.MemberId, member.DisplayName, member.IsActive);

    public static ServicePriceChangeDto ToPriceChangeDto(ServicePriceChange change, MemberRefDto? changedBy) => new(
        change.Id,
        change.PreviousPrice,
        change.PreviousCurrency,
        change.NewPrice,
        change.NewCurrency,
        change.Reason,
        changedBy,
        change.OccurredAt,
        change.Origin.ToString());

    public static IpcRateDto ToIpcRateDto(IpcRate rate, bool inUse, MemberRefDto? updatedBy) => new(
        rate.Year,
        rate.Rate,
        inUse,
        updatedBy,
        rate.UpdatedAt,
        rate.Version);

    public static ProfileDto ToProfileDto(CustomerBillingProfile profile, MemberRefDto assignee) => new(
        profile.CustomerId,
        profile.Channel.ToString(),
        assignee,
        profile.Version);
}

/// <summary>Un total por moneda, nunca convertido («$ 450.000 y US$ 20,00»): compartido por la lista, el servicio y los mensajes.</summary>
public static class CurrencyTotals
{
    public static IReadOnlyList<CurrencyAmountDto> Of(IEnumerable<(string Currency, decimal Amount)> amounts) => amounts
        .GroupBy(item => item.Currency, StringComparer.Ordinal)
        .Select(group => new CurrencyAmountDto(group.Key, group.Sum(item => item.Amount)))
        .OrderBy(total => Currencies.OrderOf(total.Currency))
        .ThenBy(total => total.Currency, StringComparer.Ordinal)
        .ToList();
}
