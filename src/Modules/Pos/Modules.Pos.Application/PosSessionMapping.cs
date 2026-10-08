using Modules.Pos.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

internal static class PosSessionMapping
{
    public static IReadOnlyList<PosPaymentTotalResponse> PaymentTotals(CashSession session) =>
    [
        new(nameof(PosPaymentMethod.Cash), session.CashTotal),
        new(nameof(PosPaymentMethod.Card), session.CardTotal),
        new(nameof(PosPaymentMethod.Transfer), session.TransferTotal),
    ];

    public static PosOpenSessionResponse ToOpenResponse(CashSession session, TenantCalendar calendar)
    {
        var openedLocal = calendar.ToLocal(session.OpenedAt);
        return new PosOpenSessionResponse(
            session.Id.Value,
            session.Status.ToString(),
            session.OpenedAt,
            openedLocal,
            DateOnly.FromDateTime(openedLocal.DateTime) < calendar.Today,
            new PosCompanyOption(session.CompanyId, session.CompanyName, session.CompanyTaxId),
            session.OpeningFloat,
            session.SalesCount,
            session.VoidedCount,
            session.SalesTotal,
            session.LiveExpectedCash,
            session.Version,
            PaymentTotals(session),
            session.Currency);
    }

    public static PosSessionSummaryResponse ToSummary(CashSession session, TenantCalendar calendar) =>
        new(
            session.Id.Value,
            session.Status.ToString(),
            session.CashierName,
            new PosCompanyHeader(session.CompanyName, session.CompanyTaxId),
            calendar.ToLocal(session.OpenedAt),
            session.ClosedAt is { } closedAt ? calendar.ToLocal(closedAt) : null,
            session.OpeningFloat,
            session.Currency,
            session.SalesCount,
            session.VoidedCount,
            session.SalesTotal,
            PaymentTotals(session),
            session.ExpectedCash ?? session.LiveExpectedCash,
            session.CountedCash,
            session.CashDifference,
            session.ClosingNote);
}
