using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Scheduling.Application;

public sealed record SearchAppointmentCustomersQuery(Guid TenantId, string? Search) : IQuery<IReadOnlyList<AppointmentCustomerDto>>;

public sealed record CreateAppointmentCustomerCommand(Guid TenantId, NewCustomerRequest Request) : ICommand<AppointmentCustomerDto>;

internal static class AppointmentCustomerRules
{
    /// <summary>The same page size as the register of the POS: enough to pick from, never the whole padrón.</summary>
    public const int Take = 20;
}

/// <summary>Busca el cliente de la cita sin darle a la Agenda el padrón de Clientes (plan 2026-11-21, corrección 5): sólo <c>appointment.manage</c>.</summary>
public sealed class SearchAppointmentCustomersHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISchedulingCustomerLookup customers)
    : IQueryHandler<SearchAppointmentCustomersQuery, IReadOnlyList<AppointmentCustomerDto>>
{
    public async Task<IReadOnlyList<AppointmentCustomerDto>> HandleAsync(
        SearchAppointmentCustomersQuery query, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, cancellationToken,
            SchedulingPermissions.AppointmentRead, SchedulingPermissions.AppointmentManage);

        var found = await customers.SearchAsync(query.TenantId, query.Search?.Trim(), AppointmentCustomerRules.Take, cancellationToken);
        return [.. found.Select(customer => new AppointmentCustomerDto(customer.Id, customer.Name, customer.Phone))];
    }
}

/// <summary>Da de alta una ficha incompleta (nombre y, si lo hay, teléfono) para agendarle una cita; Clientes decide las reglas del alta.</summary>
public sealed class CreateAppointmentCustomerHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISchedulingCustomerLookup customers,
    IValidator<NewCustomerRequest> validator)
    : ICommandHandler<CreateAppointmentCustomerCommand, AppointmentCustomerDto>
{
    public async Task<AppointmentCustomerDto> HandleAsync(CreateAppointmentCustomerCommand command, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken,
            SchedulingPermissions.AppointmentRead, SchedulingPermissions.AppointmentManage);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);

        var created = await customers.CreateAsync(
            command.TenantId, new NewSchedulingCustomer(command.Request.Name!.Trim(), command.Request.Phone?.Trim()), cancellationToken);
        return new AppointmentCustomerDto(created.Id, created.Name, created.Phone);
    }
}
