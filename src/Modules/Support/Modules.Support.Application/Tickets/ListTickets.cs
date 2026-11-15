using BuildingBlocks.Application;
using FluentValidation;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <summary>
/// La bandeja. <c>Status</c> admite varios estados por nombre separados por coma; <c>Assignee</c> es <c>me</c>, <c>unassigned</c> o
/// el id de una membresía; <c>Due</c> es <c>overdue</c> o <c>dueSoon</c>; <c>Search</c> es el radicado o un texto. Todo vacío no
/// filtra. Va por <c>receivedAt</c> descendente.
/// </summary>
public sealed record ListTicketsQuery(
    Guid TenantId, string? Status, Guid? TypeId, string? Assignee, string? Due, string? Search, int Page, int PageSize)
    : IQuery<SupportTicketPageDto>
{
    public const int DefaultPageSize = 25;

    public const int MaxPageSize = 100;

    public const int SearchMaxLength = 200;

    public const string AssigneeMe = "me";

    public const string AssigneeUnassigned = "unassigned";

    public const string DueOverdue = "overdue";

    public const string DueSoon = "dueSoon";
}

public sealed class ListTicketsValidator : AbstractValidator<ListTicketsQuery>
{
    public ListTicketsValidator()
    {
        RuleFor(query => query.Status)
            .Must(status => ListTicketsHandler.TryParseStatuses(status, out _))
            .WithMessage("Status must list ticket statuses by name, separated by commas.");
        RuleFor(query => query.Assignee)
            .Must(assignee => string.IsNullOrWhiteSpace(assignee) || assignee is ListTicketsQuery.AssigneeMe or ListTicketsQuery.AssigneeUnassigned ||
                Guid.TryParse(assignee, out _))
            .WithMessage("Assignee must be me, unassigned or a member id.");
        RuleFor(query => query.Due)
            .Must(due => string.IsNullOrWhiteSpace(due) || due is ListTicketsQuery.DueOverdue or ListTicketsQuery.DueSoon)
            .WithMessage("Due must be overdue or dueSoon.");
        RuleFor(query => query.Search).MaximumLength(ListTicketsQuery.SearchMaxLength);
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, ListTicketsQuery.MaxPageSize);
    }
}

/// <summary>
/// «Vencidos»: <c>due_on</c> antes de hoy con el reloj corriendo. «Por vencer»: <c>due_on</c> entre hoy y el día hábil número
/// <c>DueSoonBusinessDays</c> después de hoy, con el reloj corriendo y el umbral del tenant [owner 2026-11-14]; con umbral 0 no hay
/// «por vencer». La búsqueda es por radicado si el texto lo es (<c>SOP-000002</c> o <c>2</c>); si no, por asunto, nombres del
/// solicitante y clientes cuyo nombre coincide. El resumen es del tenant entero y no cambia con los filtros.
/// </summary>
public sealed class ListTicketsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IMembershipDirectory membershipDirectory,
    ISupportProvisioner provisioner,
    ISupportTicketRepository tickets,
    ISupportCustomerLookup customers,
    SupportTicketDtoComposer composer,
    IValidator<ListTicketsQuery> validator)
    : IQueryHandler<ListTicketsQuery, SupportTicketPageDto>
{
    internal static bool TryParseStatuses(string? text, out IReadOnlyCollection<SupportTicketStatus> statuses)
    {
        statuses = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var parsed = new List<SupportTicketStatus>();
        foreach (var part in text.Split(','))
        {
            if (!SupportTicketRules.TryParseExact<SupportTicketStatus>(part.Trim(), out var status))
            {
                return false;
            }

            parsed.Add(status);
        }

        statuses = parsed;
        return true;
    }

    public async Task<SupportTicketPageDto> HandleAsync(ListTicketsQuery query, CancellationToken cancellationToken)
    {
        await SupportAuthorization.EnsureCanViewAsync(executionContext, tenantModules, query.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);
        if (!TryParseStatuses(query.Status, out var statuses))
        {
            throw new InvalidOperationException($"Status '{query.Status}' passed validation but is not a list of ticket statuses.");
        }

        await provisioner.EnsureAsync(query.TenantId, cancellationToken);
        var context = await composer.ReadContextAsync(query.TenantId, cancellationToken);
        var dueSoonLimit = context.DueSoonBusinessDays > 0
            ? SupportBusinessDays.Add(context.Clock.Calendar, context.Today, context.DueSoonBusinessDays)
            : (DateOnly?)null;

        var filter = new SupportTicketFilter(
            query.TenantId,
            statuses,
            query.TypeId,
            AssigneeMemberId: null,
            UnassignedOnly: query.Assignee == ListTicketsQuery.AssigneeUnassigned,
            RunningOnly: false,
            DueBefore: null,
            DueBetween: null,
            Number: null,
            SearchTerm: null,
            SearchCustomerIds: null,
            query.Page,
            query.PageSize);
        filter = filter with { AssigneeMemberId = await AssigneeOfAsync(query, cancellationToken) };
        filter = WithDue(filter, query.Due, context.Today, dueSoonLimit);
        filter = await WithSearchAsync(filter, query.Search, cancellationToken);

        // Con el umbral en 0, «por vencer» no tiene ventana: no se consulta.
        var page = query.Due == ListTicketsQuery.DueSoon && dueSoonLimit is null
            ? new SupportTicketPage([], 0)
            : await tickets.ListAsync(filter, cancellationToken);
        var items = await composer.ComposeListAsync(query.TenantId, page.Items, context, cancellationToken);
        var totals = await tickets.TotalsAsync(query.TenantId, context.Today, dueSoonLimit, cancellationToken);

        return new SupportTicketPageDto(
            items,
            page.Total,
            query.Page,
            query.PageSize,
            new SupportInboxSummaryDto(
                SupportTicketStatuses.Ordered.Select(status => new SupportStatusCountDto(status.ToString(), totals.ByStatus[status])).ToList(),
                totals.Overdue,
                totals.DueSoon));
    }

    // «me» es la membresía de quien llama: sin membresía activa en el tenant no es nadie, y eso es 403 como en las acciones.
    private async Task<Guid?> AssigneeOfAsync(ListTicketsQuery query, CancellationToken cancellationToken) =>
        query.Assignee switch
        {
            ListTicketsQuery.AssigneeMe => await membershipDirectory.FindActiveMembershipIdAsync(
                    executionContext.SubjectId, query.TenantId, cancellationToken)
                ?? throw SupportAuthorization.Denied(),
            { } text when Guid.TryParse(text, out var memberId) => memberId,
            _ => null,
        };

    private static SupportTicketFilter WithDue(SupportTicketFilter filter, string? due, DateOnly today, DateOnly? dueSoonLimit) =>
        due switch
        {
            ListTicketsQuery.DueOverdue => filter with { RunningOnly = true, DueBefore = today },
            ListTicketsQuery.DueSoon when dueSoonLimit is { } limit => filter with { RunningOnly = true, DueBetween = (today, limit) },
            _ => filter,
        };

    private async Task<SupportTicketFilter> WithSearchAsync(SupportTicketFilter filter, string? search, CancellationToken cancellationToken)
    {
        var term = search?.Trim();
        if (string.IsNullOrEmpty(term))
        {
            return filter;
        }

        if (SupportTicketNumber.TryParse(term, out var number))
        {
            return filter with { Number = number };
        }

        // El nombre del cliente vive en el esquema de Customers: se resuelve acá y el repositorio filtra por esos ids.
        var customerIds = await customers.SearchIdsAsync(filter.TenantId, term, cancellationToken);
        return filter with { SearchTerm = term, SearchCustomerIds = customerIds };
    }
}
