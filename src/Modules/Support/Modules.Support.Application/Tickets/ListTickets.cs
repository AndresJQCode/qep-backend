using BuildingBlocks.Application;
using FluentValidation;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <summary>
/// La bandeja. <c>Status</c> admite varios estados por nombre separados por coma; <c>Assignee</c> es <c>me</c>, <c>unassigned</c> o
/// el id de una membresía; <c>Due</c> es <c>overdue</c> o <c>dueSoon</c>; <c>Search</c> es el radicado o un texto; <c>Priority</c>
/// admite varias prioridades por nombre separadas por coma, como <c>Status</c>. Todo vacío no filtra. <c>Sort</c> vacío o
/// <c>received</c> va por <c>receivedAt</c> descendente; <c>priority</c>, de <c>Urgent</c> a <c>Low</c> y dentro de cada nivel por
/// <c>receivedAt</c> descendente (spec 2026-11-15 §5.3).
/// </summary>
public sealed record ListTicketsQuery(
    Guid TenantId,
    string? Status,
    Guid? TypeId,
    string? Assignee,
    string? Due,
    string? Search,
    string? Priority,
    string? Sort,
    int Page,
    int PageSize,
    string? Kind = null,
    string? CreatedBy = null)
    : IQuery<SupportTicketPageDto>
{
    public const string KindExternal = "external";

    public const string KindInternal = "internal";

    public const string KindAll = "all";

    public const string CreatedByMe = "me";

    public const string SortReceived = "received";

    public const string SortPriority = "priority";

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
        RuleFor(query => query.Priority)
            .Must(priority => ListTicketsHandler.TryParsePriorities(priority, out _))
            .WithMessage("Priority must list ticket priorities by name, separated by commas.");
        RuleFor(query => query.Sort)
            .Must(sort => string.IsNullOrWhiteSpace(sort) || sort is ListTicketsQuery.SortReceived or ListTicketsQuery.SortPriority)
            .WithMessage("Sort must be received or priority.");
        RuleFor(query => query.Kind)
            .Must(kind => string.IsNullOrWhiteSpace(kind) ||
                kind is ListTicketsQuery.KindExternal or ListTicketsQuery.KindInternal or ListTicketsQuery.KindAll)
            .WithMessage("Kind must be external, internal or all.");
        RuleFor(query => query.CreatedBy)
            .Must(createdBy => string.IsNullOrWhiteSpace(createdBy) || createdBy == ListTicketsQuery.CreatedByMe)
            .WithMessage("CreatedBy must be me.");
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, ListTicketsQuery.MaxPageSize);
    }
}

/// <summary>
/// «Vencidos»: <c>due_on</c> antes de hoy con el reloj corriendo. «Por vencer»: <c>due_on</c> entre hoy y el día hábil número
/// <c>DueSoonBusinessDays</c> después de hoy, con el reloj corriendo y el umbral del tenant [owner 2026-11-14]; con umbral 0 no hay
/// «por vencer». La búsqueda es por radicado si el texto lo es (<c>SOP-000002</c> o <c>2</c>); si no, por asunto, nombres del
/// solicitante y clientes cuyo nombre coincide. El resumen sigue el mismo alcance que la lista (el
/// <c>kind</c> y, para quien sólo usa tareas, su participación) y no cambia con los demás filtros: así nadie cuenta tareas ajenas ni
/// tickets de clientes (spec 2026-11-22 §5.3).
/// </summary>
public sealed class ListTicketsHandler(
    IExecutionContext executionContext,
    SupportTicketScope scope,
    IMembershipDirectory membershipDirectory,
    ISupportProvisioner provisioner,
    ISupportTicketRepository tickets,
    ISupportCustomerLookup customers,
    SupportTicketDtoComposer composer,
    IValidator<ListTicketsQuery> validator)
    : IQueryHandler<ListTicketsQuery, SupportTicketPageDto>
{
    internal static bool TryParseStatuses(string? text, out IReadOnlyCollection<SupportTicketStatus> statuses) =>
        TryParseNames(text, out statuses);

    internal static bool TryParsePriorities(string? text, out IReadOnlyCollection<SupportTicketPriority> priorities) =>
        TryParseNames(text, out priorities);

    // Nombres exactos separados por coma; vacío es «todos».
    private static bool TryParseNames<TEnum>(string? text, out IReadOnlyCollection<TEnum> values)
        where TEnum : struct, Enum
    {
        values = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var parsed = new List<TEnum>();
        foreach (var part in text.Split(','))
        {
            if (!SupportTicketRules.TryParseExact<TEnum>(part.Trim(), out var value))
            {
                return false;
            }

            parsed.Add(value);
        }

        values = parsed;
        return true;
    }

    public async Task<SupportTicketPageDto> HandleAsync(ListTicketsQuery query, CancellationToken cancellationToken)
    {
        // El permiso (403) va antes que la validación, como en las demás rutas.
        var access = await scope.EnsureAccessAsync(query.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);
        var listScope = await scope.ListScopeAsync(query.TenantId, RequestedKind(query.Kind), cancellationToken);
        if (!TryParseStatuses(query.Status, out var statuses))
        {
            throw new InvalidOperationException($"Status '{query.Status}' passed validation but is not a list of ticket statuses.");
        }

        if (!TryParsePriorities(query.Priority, out var priorities))
        {
            throw new InvalidOperationException($"Priority '{query.Priority}' passed validation but is not a list of ticket priorities.");
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
            query.PageSize,
            priorities,
            ByPriority: query.Sort == ListTicketsQuery.SortPriority,
            Kind: listScope.Kind,
            ParticipantMemberId: listScope.ParticipantMemberId,
            CreatedByMemberId: query.CreatedBy == ListTicketsQuery.CreatedByMe
                ? access.MemberId ?? throw SupportAuthorization.Denied()
                : null);
        filter = filter with { AssigneeMemberId = await AssigneeOfAsync(query, cancellationToken) };
        filter = WithDue(filter, query.Due, context.Today, dueSoonLimit);
        filter = await WithSearchAsync(filter, query.Search, cancellationToken);

        // Con el umbral en 0, «por vencer» no tiene ventana: no se consulta.
        var page = query.Due == ListTicketsQuery.DueSoon && dueSoonLimit is null
            ? new SupportTicketPage([], 0)
            : await tickets.ListAsync(filter, cancellationToken);
        var items = await composer.ComposeListAsync(query.TenantId, page.Items, context, cancellationToken);
        var totals = await tickets.TotalsAsync(
            query.TenantId, listScope.Kind, listScope.ParticipantMemberId, context.Today, dueSoonLimit, cancellationToken);

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

    private static SupportTicketKind? RequestedKind(string? kind) =>
        kind switch
        {
            ListTicketsQuery.KindExternal => SupportTicketKind.External,
            ListTicketsQuery.KindInternal => SupportTicketKind.Internal,
            ListTicketsQuery.KindAll => SupportTicketKind.All,
            _ => null,
        };

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
