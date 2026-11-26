using BuildingBlocks.Application;
using FluentValidation;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;

namespace Modules.Siigo.Application.Settings;

/// <summary><c>PUT …/company</c> (<c>siigo.settings.manage</c>). <paramref name="ExpectedVersion"/> llega por <c>If-Match</c>.</summary>
public sealed record AssignSiigoCompanyCommand(Guid TenantId, Guid ConnectionId, Guid CompanyId, long ExpectedVersion)
    : ICommand<SiigoSettingsDto>;

/// <summary>La forma del cuerpo: 422 <c>validation.failed</c> con el campo en camelCase, para que el formulario marque el input.</summary>
public sealed class AssignSiigoCompanyValidator : AbstractValidator<AssignSiigoCompanyCommand>
{
    public AssignSiigoCompanyValidator()
    {
        RuleFor(command => command.CompanyId).NotEmpty().OverridePropertyName("companyId");
        RuleFor(command => command.ExpectedVersion).GreaterThanOrEqualTo(0).OverridePropertyName("expectedVersion");
    }
}

/// <summary>
/// Spec 2026-11-18 §5.3, paso 2. En orden: tenant, permiso y módulo (403); la forma (422); la conexión del tenant (403); la versión
/// (412 <c>concurrency.conflict</c>); la empresa del tenant y activa (422 <c>siigo.settings.company_invalid</c>). Si otra conexión del
/// tenant ya factura con esa empresa, el guardado choca con <c>IX_settings_tenant_company</c> y <c>SiigoUnitOfWork</c> lo traduce, por
/// nombre de índice, a 409 <c>siigo.settings.company_taken</c>: la comprobación previa sería sólo una respuesta amable y la carrera
/// la cerraría el índice igual (salvo que quien la tiene ya no tenga conexión: esa fila huérfana se borra antes). Audita <c>siigo.settings.updated</c> con el nombre del paso, en la misma transacción.
/// </summary>
public sealed class AssignSiigoCompanyHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISiigoConnectionDirectory connections,
    ISiigoSettingsRepository settingsRepository,
    ISiigoCompanyDirectory companies,
    ISiigoTaxRateDirectory taxRates,
    ISiigoMemberDirectory members,
    ISiigoUnitOfWork unitOfWork,
    ISiigoAuditRecorder auditRecorder,
    IClock clock,
    IValidator<AssignSiigoCompanyCommand> validator) : ICommandHandler<AssignSiigoCompanyCommand, SiigoSettingsDto>
{
    public async Task<SiigoSettingsDto> HandleAsync(AssignSiigoCompanyCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await SiigoSettingsSupport.AuthorizeAsync(executionContext, tenantModules, command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var connection = await SiigoSettingsSupport.ConnectionAsync(connections, command.TenantId, command.ConnectionId, cancellationToken);
        var settings = await SiigoSettingsSupport.SettingsAsync(settingsRepository, command.TenantId, command.ConnectionId, cancellationToken);
        SiigoSettingsSupport.EnsureVersion(settings, command.ExpectedVersion);

        var company = await companies.FindCompanyAsync(command.TenantId, command.CompanyId, cancellationToken);
        if (company is not { IsActive: true })
        {
            throw new SiigoDomainException(
                SiigoErrorCodes.SettingsCompanyInvalid, "The company does not exist in this tenant or is not active.");
        }

        // Deleting a connection does not clean its settings (soft reference, no FK), so a row can outlive its connection and keep the
        // company locked through IX_settings_tenant_company. Whoever holds it and no longer has a connection is released here; a live
        // holder is left alone and the index answers 409 company_taken.
        var holder = await settingsRepository.FindByCompanyAsync(command.TenantId, company.Id, [], cancellationToken);
        if (holder is not null
            && holder.ConnectionId != settings.ConnectionId
            && await connections.FindAsync(command.TenantId, holder.ConnectionId, cancellationToken) is null)
        {
            settingsRepository.Remove(holder);
        }

        settings.AssignCompany(company.Id);
        SiigoSettingsSupport.Audit(auditRecorder, executionContext, settings, SiigoSettingsSteps.Company, clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await SiigoSettingsSupport.ToDtoAsync(settings, connection, companies, taxRates, members, cancellationToken);
    }
}
