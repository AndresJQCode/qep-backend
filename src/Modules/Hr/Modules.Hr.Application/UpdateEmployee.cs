using BuildingBlocks.Application;
using FluentValidation;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Hr.Application;

/// <param name="IfMatch">La versión que la pantalla cargó; llega por <c>If-Match</c>, crudo: el caso de uso lo lee ya autorizado.</param>
public sealed record UpdateEmployeeCommand(Guid TenantId, Guid EmployeeId, EmployeeRequest Request, string? IfMatch)
    : ICommand<EmployeeDetailDto>;

/// <summary>
/// Edita la ficha: el <c>PUT</c> reemplaza todo, así que <c>country: null</c> vuelve a tomar el país del tenant. Quien edita sin
/// <c>hr.contract.read</c> recibe la ficha enmascarada, igual que en el <c>GET</c> (Review Focus 2).
/// </summary>
public sealed class UpdateEmployeeHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    ITenantDirectory tenantDirectory,
    IEmployeeRepository employees,
    IHrGeographyLookup geography,
    IHrMemberDirectory members,
    ActivityWriter activity,
    IHrUnitOfWork unitOfWork,
    IHrAuditPublisher auditPublisher,
    EmployeeDetailComposer composer,
    IValidator<EmployeeRequest> validator)
    : ICommandHandler<UpdateEmployeeCommand, EmployeeDetailDto>
{
    public async Task<EmployeeDetailDto> HandleAsync(UpdateEmployeeCommand command, CancellationToken cancellationToken)
    {
        await HrAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken, HrPermissions.EmployeeRead, HrPermissions.EmployeeManage);
        var expectedVersion = HrIfMatch.Require(command.IfMatch);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);

        var tenantId = command.TenantId;
        var request = command.Request;
        var employee = await employees.FindAsync(tenantId, new EmployeeId(command.EmployeeId), cancellationToken)
            ?? throw HrNotFound.Employee(command.EmployeeId);
        HrSettingsSupport.EnsureVersion(employee.Version, expectedVersion);

        var calendar = await tenantClock.GetAsync(tenantId, cancellationToken);
        await EmployeeRules.EnsureCityAsync(geography, request.CityId, cancellationToken);
        if (request.MemberId != employee.MemberId)
        {
            await EmployeeRules.EnsureMemberAsync(members, tenantId, request.MemberId, cancellationToken);
        }

        EmployeeRules.EnsureBirthDate(request.BirthDate, calendar.Today);
        var requested = EmployeeCountry.Normalize(request.Country);
        if (requested is not null)
        {
            EmployeeCountry.EnsureKnown(requested, new HrCountryIndex(await geography.ListCountriesAsync(cancellationToken)));
        }

        var tenantCountry = (await tenantDirectory.GetRegionalSettingsAsync(new TenantId(tenantId), cancellationToken))?.Country;
        var country = EmployeeCountry.Effective(requested, tenantCountry);
        EmployeeCountry.Ensure(country, Enum.Parse<HrDocumentType>(request.DocumentType!), request.ForeignPensionExempt ?? false);

        var before = EmployeeRules.ProfileOf(employee);
        var after = EmployeeRules.ToProfile(request, country);
        var names = await ReferenceNamesAsync(tenantId, before, after, cancellationToken);
        HrConflicts.Run(() => employee.UpdateProfile(after, calendar.UtcNow));

        var changes = ActivityChanges.Diff(before, after, names);
        if (changes.Count > 0)
        {
            await activity.AppendAsync(
                tenantId, employee, HrActivityAction.PersonalDataUpdated, HrActivityCategory.Personal, null, changes, cancellationToken);
        }

        HrSettingsSupport.PublishAudit(
            auditPublisher, executionContext, tenantId, HrAuditActions.EmployeeUpdated, HrAuditActions.Resources.Employee,
            employee.Id.Value, changes.Select(change => change.Field).ToList(), calendar.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ComposeAsync(
            employee, executionContext.HasPermission(HrPermissions.ContractRead), calendar.Today, cancellationToken);
    }

    // La ciudad y la persona vinculada van al historial por su nombre de hoy, no por el id.
    private async Task<IReadOnlyDictionary<Guid, string>> ReferenceNamesAsync(
        Guid tenantId, EmployeeProfile before, EmployeeProfile after, CancellationToken cancellationToken)
    {
        var names = new Dictionary<Guid, string>();
        var cityIds = new[] { before.CityId, after.CityId }.OfType<Guid>().Distinct().ToList();
        if (cityIds.Count > 0)
        {
            foreach (var (id, city) in await geography.FindCitiesAsync(cityIds, cancellationToken))
            {
                names[id] = city.Name;
            }
        }

        var memberIds = new[] { before.MemberId, after.MemberId }.OfType<Guid>().Distinct().ToList();
        if (memberIds.Count > 0)
        {
            foreach (var member in await members.FindAsync(tenantId, memberIds, cancellationToken))
            {
                names[member.MemberId] = member.DisplayName;
            }
        }

        return names;
    }
}
