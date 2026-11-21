using BuildingBlocks.Application;
using FluentValidation;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application;

/// <param name="Status">Nulo o el nombre exacto de <see cref="EmployeeStatus"/>.</param>
/// <param name="ExpiringSoon">Término fijo vigente con fin en 30 días o menos (o vencido). Pide <c>hr.contract.read</c>.</param>
/// <param name="CompanyId">La empresa del contrato vigente. Pide <c>hr.contract.read</c>: la empresa es dato del contrato.</param>
public sealed record ListEmployeesQuery(
    Guid TenantId, string? Search, string? Status, bool ExpiringSoon, Guid? CompanyId, int Page, int PageSize)
    : IQuery<EmployeePageDto>
{
    public const int DefaultPageSize = 25;

    public const int MaxPageSize = 100;
}

public sealed class ListEmployeesQueryValidator : HrValidator<ListEmployeesQuery>
{
    public ListEmployeesQueryValidator()
    {
        RuleFor(query => query.Status)
            .Must(status => status is null || HrEnums.TryParse<EmployeeStatus>(status, out _))
            .WithMessage("El estado no es válido: usa Active, Retired o Deactivated.");
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1).WithMessage("La página empieza en 1.");
        RuleFor(query => query.PageSize)
            .InclusiveBetween(1, ListEmployeesQuery.MaxPageSize)
            .WithMessage($"El tamaño de página va de 1 a {ListEmployeesQuery.MaxPageSize}.");
    }
}

public sealed class ListEmployeesHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IEmployeeRepository employees,
    EmployeeDetailComposer composer,
    IValidator<ListEmployeesQuery> validator)
    : IQueryHandler<ListEmployeesQuery, EmployeePageDto>
{
    public async Task<EmployeePageDto> HandleAsync(ListEmployeesQuery query, CancellationToken cancellationToken)
    {
        await HrAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, cancellationToken, HrPermissions.EmployeeRead);

        // El vencimiento y la empresa son dato del contrato: pedirlos sin leer contratos es 403, no una lista vacía.
        var canReadContract = executionContext.HasPermission(HrPermissions.ContractRead);
        if (!canReadContract && (query.ExpiringSoon || query.CompanyId is not null))
        {
            throw HrAuthorization.Denied();
        }

        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var today = (await tenantClock.GetAsync(query.TenantId, cancellationToken)).Today;
        EmployeeStatus? status = query.Status is null ? null : Enum.Parse<EmployeeStatus>(query.Status);
        var page = await employees.ListAsync(
            query.TenantId, query.Search, status, query.ExpiringSoon, query.CompanyId, today, query.Page, query.PageSize, cancellationToken);

        return await composer.ComposePageAsync(page, query.Page, query.PageSize, canReadContract, today, cancellationToken);
    }
}
