using Modules.Billing.Domain;
using Modules.Tenancy.Application;

namespace Modules.Billing.Application;

public static class BillingMapping
{
    /// <summary>
    /// <c>Status</c> es <c>Overdue</c> cuando el cobro está <c>Pending</c> y venció antes de <paramref name="today"/> (la «hoy»
    /// del tenant, no la del servidor); <c>DaysOverdue</c> es 0 si no vence.
    /// </summary>
    public static ChargeDto ToChargeDto(
        Charge charge,
        BillingService service,
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
            Math.Max(0, today.DayNumber - charge.DueDate.DayNumber),
            charge.PaidOn is { } paidOn
                ? new PaymentDto(paidOn, charge.PaymentNote, paidBy, charge.PaidAt ?? default)
                : null,
            last,
            charge.Version);

    public static ServiceDto ToServiceDto(
        BillingService service,
        CustomerRefDto customer,
        ChargeDto? nextCharge,
        int openCount,
        IReadOnlyList<CurrencyAmountDto> openAmountByCurrency) => new(
            service.Id.Value,
            customer,
            service.Name,
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
            service.UpdatedAt);

    public static ProfileDto ToProfileDto(CustomerBillingProfile profile, MemberRefDto assignee) => new(
        profile.CustomerId,
        profile.Channel.ToString(),
        assignee,
        profile.Version);
}

/// <summary>Un total por moneda, nunca convertido («$ 450.000 y USD 20»): compartido por la lista, el servicio y los mensajes.</summary>
public static class CurrencyTotals
{
    public static IReadOnlyList<CurrencyAmountDto> Of(IEnumerable<(string Currency, decimal Amount)> amounts) => amounts
        .GroupBy(item => item.Currency, StringComparer.Ordinal)
        .Select(group => new CurrencyAmountDto(group.Key, group.Sum(item => item.Amount)))
        .OrderBy(total => Currencies.OrderOf(total.Currency))
        .ThenBy(total => total.Currency, StringComparer.Ordinal)
        .ToList();
}
