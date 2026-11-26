using BuildingBlocks.Application;
using FluentValidation;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application;

public sealed record ListEmployeeActivityQuery(Guid TenantId, Guid EmployeeId, int Page, int PageSize) : IQuery<ActivityPageDto>;

public sealed class ListEmployeeActivityQueryValidator : HrValidator<ListEmployeeActivityQuery>
{
    public ListEmployeeActivityQueryValidator()
    {
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1).WithMessage("La página empieza en 1.");
        RuleFor(query => query.PageSize)
            .InclusiveBetween(1, ListEmployeesQuery.MaxPageSize)
            .WithMessage($"El tamaño de página va de 1 a {ListEmployeesQuery.MaxPageSize}.");
    }
}

/// <summary>
/// El historial de acciones, más reciente primero. Sin <c>hr.contract.read</c> trae sólo las entradas <c>Personal</c> y
/// <c>Lifecycle</c> (no cuentan en <c>total</c> las demás) y de éstas quita los cambios marcados como sensibles al escribir.
/// </summary>
public sealed class ListEmployeeActivityHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IEmployeeRepository employees,
    IEmployeeActivityLog log,
    IValidator<ListEmployeeActivityQuery> validator)
    : IQueryHandler<ListEmployeeActivityQuery, ActivityPageDto>
{
    private static readonly IReadOnlySet<HrActivityCategory> Visible =
        new HashSet<HrActivityCategory> { HrActivityCategory.Personal, HrActivityCategory.Lifecycle };

    public async Task<ActivityPageDto> HandleAsync(ListEmployeeActivityQuery query, CancellationToken cancellationToken)
    {
        await HrAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, cancellationToken, HrPermissions.EmployeeRead);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        _ = await employees.FindAsync(query.TenantId, new EmployeeId(query.EmployeeId), cancellationToken)
            ?? throw HrNotFound.Employee(query.EmployeeId);

        var canReadContract = executionContext.HasPermission(HrPermissions.ContractRead);
        // Medical (spec 2026-11-23 §10) is not part of contract visibility: it needs hr.medical.read on its own.
        var categories = canReadContract ? Enum.GetValues<HrActivityCategory>().ToHashSet() : [.. Visible];
        if (!executionContext.HasPermission(HrPermissions.MedicalRead))
        {
            categories.Remove(HrActivityCategory.Medical);
        }
        else
        {
            categories.Add(HrActivityCategory.Medical);
        }

        var (items, total) = await log.ListAsync(
            query.TenantId, new EmployeeId(query.EmployeeId), categories, query.Page, query.PageSize, cancellationToken);

        return new ActivityPageDto(
            items.Select(entry => new ActivityEntryDto(
                entry.Id,
                entry.OccurredAt,
                new ActivityActorDto(entry.ActorSubjectId, entry.ActorName),
                entry.Action.ToString(),
                entry.Category.ToString(),
                entry.ContractId,
                entry.Changes
                    .Where(change => canReadContract || !change.Sensitive)
                    .Select(change => new ActivityChangeDto(change.Field, change.Before, change.After))
                    .ToList())).ToList(),
            total,
            query.Page,
            query.PageSize);
    }
}
