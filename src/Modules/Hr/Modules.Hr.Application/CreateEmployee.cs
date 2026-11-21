using BuildingBlocks.Application;
using FluentValidation;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Hr.Application;

public sealed record CreateEmployeeCommand(Guid TenantId, CreateEmployeeRequest Request) : ICommand<EmployeeDetailDto>;

/// <summary>
/// El alta: el empleado, su pago opcional y su primer contrato con su salario y su cargo, todo o nada. Autoriza los cuatro permisos
/// de empleados y contratos. Todo lo que se puede rechazar con un error de campo se rechaza antes de tocar el agregado.
/// </summary>
public sealed class CreateEmployeeHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    ITenantDirectory tenantDirectory,
    HrSetup setup,
    IEmployeeRepository employees,
    IHrGeographyLookup geography,
    IHrMemberDirectory members,
    NewContractFactory contractFactory,
    ActivityWriter activity,
    IHrUnitOfWork unitOfWork,
    IHrAuditPublisher auditPublisher,
    EmployeeDetailComposer composer,
    IValidator<CreateEmployeeRequest> validator)
    : ICommandHandler<CreateEmployeeCommand, EmployeeDetailDto>
{
    public async Task<EmployeeDetailDto> HandleAsync(CreateEmployeeCommand command, CancellationToken cancellationToken)
    {
        await HrAuthorization.EnsureAsync(
            executionContext,
            tenantModules,
            command.TenantId,
            cancellationToken,
            HrPermissions.EmployeeRead,
            HrPermissions.EmployeeManage,
            HrPermissions.ContractRead,
            HrPermissions.ContractManage);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);

        var tenantId = command.TenantId;
        var request = command.Request;
        if (!await setup.IsCompleteAsync(tenantId, cancellationToken))
        {
            throw new HrDomainException(
                HrErrorCodes.SetupIncomplete, "Configura una empresa activa con sede y centro de trabajo, y un cargo, antes de dar de alta.");
        }

        var calendar = await tenantClock.GetAsync(tenantId, cancellationToken);
        await EmployeeRules.EnsureCityAsync(geography, request.CityId, cancellationToken);
        await EmployeeRules.EnsureMemberAsync(members, tenantId, request.MemberId, cancellationToken);
        EmployeeRules.EnsureBirthDate(request.BirthDate, calendar.Today);
        var country = await ResolveCountryAsync(tenantId, request, cancellationToken);

        var (contract, floor) = await contractFactory.BuildAsync(
            tenantId, request.Contract!, "contract.", requireActiveCompany: true, cancellationToken);
        var profile = EmployeeRules.ToProfile(request, country);
        var payment = EmployeeRules.ToPayment(request.Payment);
        var names = await activity.NamesAsync(tenantId, [contract.Terms.CompanyId], cancellationToken);

        var employee = HrConflicts.Run(() => Employee.Hire(
            EmployeeId.New(), tenantId, profile, payment, contract, floor, calendar.UtcNow));
        employees.Add(employee);

        var changes = ActivityChanges.EmployeeCreated(profile, payment, employee.ActiveContract!, names);
        await activity.AppendAsync(
            tenantId, employee, HrActivityAction.EmployeeCreated, HrActivityCategory.Lifecycle, contract.Id.Value, changes, cancellationToken);
        HrSettingsSupport.PublishAudit(
            auditPublisher, executionContext, tenantId, HrAuditActions.EmployeeCreated, HrAuditActions.Resources.Employee,
            employee.Id.Value, changes.Select(change => change.Field).ToList(), calendar.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ComposeAsync(
            employee, executionContext.HasPermission(HrPermissions.ContractRead), calendar.Today, cancellationToken);
    }

    // El país pedido tiene que estar en el catálogo de Geografía; sin país, el del tenant (que no se revalida) o Colombia. Después
    // se cruza con el documento: el cruce depende del país efectivo, por eso no vive en el validador.
    private async Task<string> ResolveCountryAsync(Guid tenantId, EmployeeRequest request, CancellationToken cancellationToken)
    {
        var requested = EmployeeCountry.Normalize(request.Country);
        if (requested is not null)
        {
            EmployeeCountry.EnsureKnown(requested, new HrCountryIndex(await geography.ListCountriesAsync(cancellationToken)));
        }

        var tenantCountry = (await tenantDirectory.GetRegionalSettingsAsync(new TenantId(tenantId), cancellationToken))?.Country;
        var country = EmployeeCountry.Effective(requested, tenantCountry);
        EmployeeCountry.Ensure(country, Enum.Parse<HrDocumentType>(request.DocumentType!), request.ForeignPensionExempt ?? false);
        return country;
    }
}
