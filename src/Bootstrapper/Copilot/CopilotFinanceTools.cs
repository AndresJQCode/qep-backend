using Modules.Ai.Application;
using Modules.Ai.Domain;
using Modules.Loans.Application;
using Modules.ServiceContracts.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.Copilot;

/// <summary>
/// Spec 2026-11-17 (copiloto) §6 y plan fase 2 C3.3: préstamos y contratos de servicio, con la misma consulta y los mismos defaults que
/// <c>LoansEndpoints</c> y <c>ServiceContractsEndpoints</c>. Los totales (capital pendiente, cuotas por cobrar, vencidos por moneda) son el
/// <c>summary</c> que calcula cada handler, nunca una suma de las 20 filas (D-C5). Los contratos de servicio se gatean con la clave de
/// módulo <c>billing</c>: el renombre del 2026-10-30 fue de código y copy, no de clave. El teléfono y el correo del cliente no viajan: no
/// hacen falta para contestar y son datos personales.
/// </summary>
internal static class CopilotFinanceTools
{
    public static IReadOnlyList<ICopilotTool> All() =>
    [
        new SearchLoansTool(),
        new GetLoanTool(),
        new ListInstallmentsTool(),
        new SearchServicesTool(),
        new GetServiceTool(),
        new ListChargesTool(),
    ];

    public static CopilotCitation LoanRef(Guid id, string name) => new(CopilotCitationKinds.Loan, id.ToString(), name);

    public static CopilotCitation ServiceRef(Guid id, string name) => new(CopilotCitationKinds.Service, id.ToString(), name);

    /// <summary>Un cobro no tiene pantalla propia: el frontend lo muestra como cita sin enlace. La etiqueta dice de qué servicio y cuándo vence.</summary>
    public static CopilotCitation ChargeRef(ChargeDto charge) =>
        new(CopilotCitationKinds.Charge, charge.Id.ToString(), $"{charge.ServiceName} · {charge.DueDate:yyyy-MM-dd}");

    public static CopilotCitation CustomerRef(Guid id, string name) => new(CopilotCitationKinds.Customer, id.ToString(), name);

    public static object Charge(ChargeDto charge) => new
    {
        @ref = ChargeRef(charge),
        charge.ServiceId,
        charge.ServiceName,
        customer = charge.Customer.Name,
        customerId = charge.Customer.Id,
        assignee = charge.Assignee?.DisplayName,
        charge.Amount,
        charge.Currency,
        charge.DueDate,
        charge.Status,
        charge.DaysOverdue,
        paidOn = charge.Payment?.PaidOn,
        lastReminder = charge.LastReminder is { } reminder ? new { reminder.Step, reminder.Channel, reminder.Status, reminder.AttemptedAt } : null,
    };

    /// <summary>Las filas más recientes primero, a lo sumo 20, con cuántas hay.</summary>
    public static object Capped<TSource>(IEnumerable<TSource> rows, Func<TSource, object> map)
    {
        var all = rows.ToList();
        return new { items = all.Take(CopilotReportTools.PageSize).Select(map).ToList(), total = all.Count, truncated = all.Count > CopilotReportTools.PageSize };
    }
}

internal sealed class SearchLoansTool : ICopilotTool
{
    public CopilotToolContract Contract { get; } = CopilotReportTools.Contract(
        "search_loans",
        "Busca préstamos otorgados por la empresa, 20 por página, por estado y por nombre o cliente. summary trae los totales del tenant (capital pendiente, cuotas por cobrar e interés causado por moneda): es la única fuente de totales.",
        LoansPermissions.Read,
        TenantModuleKeys.Loans,
        """
        "status":{"type":["string","null"],"description":"active o settled; null es active."},"search":{"type":["string","null"],"description":"Texto a buscar en el nombre del préstamo o del cliente, o null."}
        """,
        CopilotReportTools.Page);

    public async Task<CopilotToolResult> ExecuteAsync(CopilotToolCall call, CancellationToken cancellationToken)
    {
        var page = await call.Dispatcher.QueryAsync(
            new ListLoansQuery(
                call.Principal.TenantId,
                CopilotArguments.Code(call.Arguments, "status") ?? ListLoansQuery.DefaultStatus,
                CopilotArguments.Search(call.Arguments, "search"),
                CopilotArguments.Page(call.Arguments),
                CopilotReportTools.PageSize),
            cancellationToken);
        var list = CopilotReportList<object>.Of(page.Items, page.Total, page.Page, loan => new
        {
            @ref = CopilotFinanceTools.LoanRef(loan.Id, loan.Name),
            loan.Name,
            customer = loan.Customer.Name,
            customerId = loan.Customer.Id,
            assignee = loan.Assignee?.DisplayName,
            loan.Status,
            loan.Currency,
            loan.PrincipalAmount,
            loan.OutstandingPrincipal,
            loan.MonthlyRatePercent,
            loan.DisbursedOn,
            loan.PendingCount,
            loan.PendingAmount,
            loan.NextCutoffDate,
        });
        return CopilotToolResult.Ok(
            new { list.Items, list.Total, list.Page, list.Truncated, summary = page.Summary },
            [.. page.Items.Take(CopilotReportTools.PageSize).Select(loan => CopilotFinanceTools.LoanRef(loan.Id, loan.Name))]);
    }
}

internal sealed class GetLoanTool : ICopilotTool
{
    public CopilotToolContract Contract { get; } = CopilotReportTools.Contract(
        "get_loan",
        "El detalle de un préstamo: condiciones, capital pendiente, cuotas pendientes, interés causado del período en curso, las 20 cuotas más recientes y los abonos a capital.",
        LoansPermissions.Read,
        TenantModuleKeys.Loans,
        """
        "loanId":{"type":"string","description":"Id del préstamo (uuid), tomado de una búsqueda."}
        """);

    public async Task<CopilotToolResult> ExecuteAsync(CopilotToolCall call, CancellationToken cancellationToken)
    {
        var detail = await call.Dispatcher.QueryAsync(
            new GetLoanQuery(call.Principal.TenantId, CopilotArguments.RequiredId(call.Arguments, "loanId")), cancellationToken);
        var loan = detail.Loan;
        var reference = CopilotFinanceTools.LoanRef(loan.Id, loan.Name);
        return CopilotToolResult.Ok(
            new
            {
                @ref = reference,
                loan.Name,
                customer = loan.Customer.Name,
                customerId = loan.Customer.Id,
                assignee = loan.Assignee?.DisplayName,
                loan.Type,
                loan.Status,
                loan.Currency,
                loan.PrincipalAmount,
                loan.OutstandingPrincipal,
                loan.MonthlyRatePercent,
                loan.DisbursedOn,
                loan.CutoffRule,
                loan.PendingCount,
                loan.PendingAmount,
                loan.AccruedToDate,
                loan.NextCutoffDate,
                loan.Note,
                currentPeriod = detail.CurrentPeriod is { } period ? new { period.Start, period.End, period.Days, period.AccruedAmount } : null,
                detail.CapitalPaymentBlock,
                installments = CopilotFinanceTools.Capped(
                    detail.Installments.OrderByDescending(installment => installment.PeriodEnd),
                    installment => new
                    {
                        installment.PeriodStart,
                        installment.PeriodEnd,
                        installment.Amount,
                        installment.Currency,
                        installment.Status,
                        paidOn = installment.Payment?.PaidOn,
                    }),
                capitalPayments = CopilotFinanceTools.Capped(
                    detail.CapitalPayments.OrderByDescending(payment => payment.PaidOn),
                    payment => new { payment.Amount, payment.PaidOn, payment.BalanceAfter, voided = payment.VoidedAt is not null }),
            },
            [reference, CopilotFinanceTools.CustomerRef(loan.Customer.Id, loan.Customer.Name)]);
    }
}

internal sealed class ListInstallmentsTool : ICopilotTool
{
    public CopilotToolContract Contract { get; } = CopilotReportTools.Contract(
        "list_installments",
        "Las cuotas de préstamos por cobrar, 20 por página, de la más antigua a la más nueva. summary trae cuántas hay, cuánto suman por moneda y la más antigua: es la única fuente de totales.",
        LoansPermissions.Read,
        TenantModuleKeys.Loans,
        """
        "search":{"type":["string","null"],"description":"Texto a buscar en el préstamo o el cliente, o null."}
        """,
        CopilotReportTools.Page);

    public async Task<CopilotToolResult> ExecuteAsync(CopilotToolCall call, CancellationToken cancellationToken)
    {
        var page = await call.Dispatcher.QueryAsync(
            new ListLoanInstallmentsQuery(
                call.Principal.TenantId, CopilotArguments.Search(call.Arguments, "search"), CopilotArguments.Page(call.Arguments), CopilotReportTools.PageSize),
            cancellationToken);
        var list = CopilotReportList<object>.Of(page.Items, page.Total, page.Page, installment => new
        {
            @ref = CopilotFinanceTools.LoanRef(installment.LoanId, installment.LoanName),
            installment.LoanName,
            customer = installment.Customer.Name,
            customerId = installment.Customer.Id,
            assignee = installment.Assignee?.DisplayName,
            installment.PeriodStart,
            installment.PeriodEnd,
            installment.Amount,
            installment.Currency,
        });
        return CopilotToolResult.Ok(
            new { list.Items, list.Total, list.Page, list.Truncated, summary = page.Summary },
            [.. page.Items.Take(CopilotReportTools.PageSize).Select(installment => CopilotFinanceTools.LoanRef(installment.LoanId, installment.LoanName))]);
    }
}

internal sealed class SearchServicesTool : ICopilotTool
{
    public CopilotToolContract Contract { get; } = CopilotReportTools.Contract(
        "search_services",
        "Busca contratos de servicio, 20 por página, por estado, cliente o nombre. summary trae los activos, los vencidos y el valor mensual por moneda: es la única fuente de totales.",
        ServiceContractsPermissions.Read,
        TenantModuleKeys.ServiceContracts,
        """
        "status":{"type":["string","null"],"description":"active, paused, cancelled o all; null es active."},"customerId":{"type":["string","null"],"description":"Id del cliente (uuid) o null."},"search":{"type":["string","null"],"description":"Texto a buscar en el servicio o el cliente, o null."}
        """,
        CopilotReportTools.Page);

    public async Task<CopilotToolResult> ExecuteAsync(CopilotToolCall call, CancellationToken cancellationToken)
    {
        var page = await call.Dispatcher.QueryAsync(
            new ListServicesQuery(
                call.Principal.TenantId,
                CopilotArguments.Id(call.Arguments, "customerId"),
                CopilotArguments.Code(call.Arguments, "status") ?? ListServicesQuery.DefaultStatus,
                CopilotArguments.Search(call.Arguments, "search"),
                CopilotArguments.Page(call.Arguments),
                CopilotReportTools.PageSize),
            cancellationToken);
        var list = CopilotReportList<object>.Of(page.Items, page.Total, page.Page, service => new
        {
            @ref = CopilotFinanceTools.ServiceRef(service.Id, service.Name),
            service.Name,
            customer = service.Customer.Name,
            customerId = service.Customer.Id,
            service.Status,
            service.Price,
            service.Currency,
            service.Cycle,
            service.OpenCount,
            service.OpenAmountByCurrency,
            nextDueDate = service.NextCharge?.DueDate,
        });
        return CopilotToolResult.Ok(
            new { list.Items, list.Total, list.Page, list.Truncated, summary = page.Summary },
            [.. page.Items.Take(CopilotReportTools.PageSize).Select(service => CopilotFinanceTools.ServiceRef(service.Id, service.Name))]);
    }
}

internal sealed class GetServiceTool : ICopilotTool
{
    public CopilotToolContract Contract { get; } = CopilotReportTools.Contract(
        "get_service",
        "El detalle de un contrato de servicio: precio, ciclo, estado, encargado, los 20 cobros más recientes y los cambios de precio.",
        ServiceContractsPermissions.Read,
        TenantModuleKeys.ServiceContracts,
        """
        "serviceId":{"type":"string","description":"Id del contrato de servicio (uuid), tomado de una búsqueda."}
        """);

    public async Task<CopilotToolResult> ExecuteAsync(CopilotToolCall call, CancellationToken cancellationToken)
    {
        var detail = await call.Dispatcher.QueryAsync(
            new GetServiceQuery(call.Principal.TenantId, CopilotArguments.RequiredId(call.Arguments, "serviceId")), cancellationToken);
        var service = detail.Service;
        var reference = CopilotFinanceTools.ServiceRef(service.Id, service.Name);
        var charges = detail.Charges.OrderByDescending(charge => charge.DueDate).ToList();
        return CopilotToolResult.Ok(
            new
            {
                @ref = reference,
                service.Name,
                service.Description,
                customer = service.Customer.Name,
                customerId = service.Customer.Id,
                service.Status,
                service.Price,
                service.Currency,
                service.Cycle,
                service.FirstDueDate,
                service.OpenCount,
                service.OpenAmountByCurrency,
                channel = detail.Profile.Channel,
                assignee = detail.Profile.Assignee.DisplayName,
                charges = CopilotFinanceTools.Capped(charges, CopilotFinanceTools.Charge),
                priceChanges = CopilotFinanceTools.Capped(
                    detail.PriceChanges,
                    change => new { change.PreviousPrice, change.PreviousCurrency, change.NewPrice, change.NewCurrency, change.Reason, change.OccurredAt }),
            },
            [
                reference,
                CopilotFinanceTools.CustomerRef(service.Customer.Id, service.Customer.Name),
                .. charges.Take(CopilotReportTools.PageSize).Select(CopilotFinanceTools.ChargeRef),
            ]);
    }
}

internal sealed class ListChargesTool : ICopilotTool
{
    public CopilotToolContract Contract { get; } = CopilotReportTools.Contract(
        "list_charges",
        "Los cobros de contratos de servicio, 20 por página, por estado, cliente o sólo los de quien pregunta. summary trae abiertos y vencidos con sus montos por moneda: es la única fuente de totales.",
        ServiceContractsPermissions.Read,
        TenantModuleKeys.ServiceContracts,
        """
        "status":{"type":["string","null"],"description":"open, paid o cancelled; null es open."},"mine":{"type":["boolean","null"],"description":"true: sólo los cobros a cargo de quien pregunta."},"customerId":{"type":["string","null"],"description":"Id del cliente (uuid) o null."},"search":{"type":["string","null"],"description":"Texto a buscar en el servicio o el cliente, o null."}
        """,
        CopilotReportTools.Page);

    public async Task<CopilotToolResult> ExecuteAsync(CopilotToolCall call, CancellationToken cancellationToken)
    {
        var page = await call.Dispatcher.QueryAsync(
            new ListChargesQuery(
                call.Principal.TenantId,
                CopilotArguments.Code(call.Arguments, "status") ?? ListChargesQuery.DefaultStatus,
                CopilotArguments.Flag(call.Arguments, "mine") ?? false,
                CopilotArguments.Id(call.Arguments, "customerId"),
                CopilotArguments.Search(call.Arguments, "search"),
                CopilotArguments.Page(call.Arguments),
                CopilotReportTools.PageSize),
            cancellationToken);
        var list = CopilotReportList<object>.Of(page.Items, page.Total, page.Page, CopilotFinanceTools.Charge);
        return CopilotToolResult.Ok(
            new { list.Items, list.Total, list.Page, list.Truncated, summary = page.Summary },
            [.. page.Items.Take(CopilotReportTools.PageSize).Select(CopilotFinanceTools.ChargeRef)]);
    }
}
