using BuildingBlocks.Application;
using FluentValidation;
using Microsoft.Extensions.Logging;
using Modules.Companies.Domain;

namespace Modules.Companies.Application;

/// <summary>Lo que el sistema sabe de la empresa que da de alta. Mismos campos que <see cref="CreateCompanyCommand"/> que importan.</summary>
public sealed record ProvisionedCompanyData(
    string Name,
    string Country,
    string DocumentType,
    string TaxId,
    Guid? CityId,
    string? CityName,
    string? Address,
    string? Email,
    string? Phone);

/// <summary>
/// Alta de una empresa hecha por el sistema, no por una persona: la usa Mayorista cuando un distribuidor acepta su invitación
/// sobre el tenant reservado.
///
/// <b>No hay permiso ni <c>IExecutionContext</c></b> (spec 2026-11-23 §2): quien acepta todavía no tiene membresía en el tenant
/// nuevo, así que <c>companies.company.manage</c> no se le podría exigir. La autorización ya la dio el link de la invitación, y
/// el actor que se audita es el usuario que acepta.
///
/// <b>Hay candado por tenant</b> porque no hay índice único que la arbitre (EMP-08): dos pestañas aceptando a la vez
/// leerían las dos «no existe» y crearían dos empresas. Con el candado la segunda ve la que dejó la primera y la reutiliza.
/// </summary>
public interface ICompanyProvisioning
{
    /// <summary>Devuelve el id de la empresa creada o de la que ya tenía ese tipo y número de documento (activa o inactiva).</summary>
    Task<Guid> EnsureCompanyAsync(
        Guid tenantId,
        Guid actorUserId,
        ProvisionedCompanyData data,
        CancellationToken cancellationToken);

    /// <summary>
    /// Owner, 2026-11-24: la empresa de un tenant que ya existía (el distribuidor vinculó uno suyo) sólo se crea si **no tiene ninguna**,
    /// activa o inactiva: ese tenant ya tiene las suyas y una más sería un duplicado. Devuelve el id de la creada, o <c>null</c> si el
    /// tenant ya tenía alguna y no se escribió nada. Mismo candado que <see cref="EnsureCompanyAsync"/>, así que dos aceptaciones a la
    /// vez no crean dos.
    /// </summary>
    Task<Guid?> EnsureFirstCompanyAsync(
        Guid tenantId,
        Guid actorUserId,
        ProvisionedCompanyData data,
        CancellationToken cancellationToken);
}

/// <summary>
/// La clave de dos partes del candado del alta del sistema (20261131 ya es de <c>EmailChallengeIssuanceLock</c>: los candados de dos
/// claves comparten espacio).
/// </summary>
public static class CompanyProvisioningLock
{
    public const int Namespace = 20261132;
}

public sealed class CompanyProvisioning(
    ICompanyRepository repository,
    ICompaniesUnitOfWork unitOfWork,
    ICompaniesAuditPublisher auditPublisher,
    ICompanyGeographyLookup geographyLookup,
    IClock clock,
    IValidator<CreateCompanyCommand> validator,
    ICompanyTaxRateLookup taxRateLookup,
    ICompanyDefaultTaxRates defaultTaxRates,
    ILogger<CompanyProvisioning> logger)
    : ICompanyProvisioning
{
    private readonly CompanyCreation creation = new(
        repository, unitOfWork, auditPublisher, geographyLookup, clock, validator, taxRateLookup, defaultTaxRates, logger);

    public async Task<Guid> EnsureCompanyAsync(
        Guid tenantId,
        Guid actorUserId,
        ProvisionedCompanyData data,
        CancellationToken cancellationToken) =>
        (await ProvisionAsync(tenantId, actorUserId, data, onlyIfTenantHasNone: false, cancellationToken))!.Value;

    public Task<Guid?> EnsureFirstCompanyAsync(
        Guid tenantId,
        Guid actorUserId,
        ProvisionedCompanyData data,
        CancellationToken cancellationToken) =>
        ProvisionAsync(tenantId, actorUserId, data, onlyIfTenantHasNone: true, cancellationToken);

    private async Task<Guid?> ProvisionAsync(
        Guid tenantId,
        Guid actorUserId,
        ProvisionedCompanyData data,
        bool onlyIfTenantHasNone,
        CancellationToken cancellationToken)
    {
        var taxId = data.TaxId.Trim();

        Company created;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            await unitOfWork.LockProvisioningAsync(tenantId, cancellationToken);

            // Con el candado ya tomado, para que dos aceptaciones a la vez no lean las dos «no tiene ninguna». Sin filtro de estado:
            // una empresa desactivada sigue siendo una empresa del tenant.
            if (onlyIfTenantHasNone
                && (await repository.SearchAsync(tenantId, null, null, null, null, cancellationToken)).Count > 0)
            {
                return null;
            }

            // SearchAsync filtra con ILIKE '%x%': 900123456 también trae a 9001234567. Por eso se vuelve a filtrar en memoria con
            // igualdad exacta de tipo y número; sin eso, una empresa de otro NIT se reutilizaría como si fuera ésta. Se buscan
            // activas e inactivas: una desactivada sigue siendo «la empresa de ese documento».
            if (CompanyDocumentTypes.TryParse(data.DocumentType, out var documentType))
            {
                var candidates = await repository.SearchAsync(tenantId, null, null, taxId, null, cancellationToken);
                var existing = candidates.FirstOrDefault(company =>
                    company.DocumentType == documentType
                    && string.Equals(company.TaxId, taxId, StringComparison.Ordinal));
                if (existing is not null)
                {
                    return existing.Id.Value;
                }
            }

            // Un tipo que no se reconoce no busca nada: CreateAsync lo rechaza con la ValidationException del campo.
            var result = await creation.CreateAsync(
                new CreateCompanyCommand(
                    tenantId,
                    data.Name,
                    [],
                    data.TaxId,
                    data.Country,
                    data.DocumentType,
                    data.CityId,
                    data.CityName,
                    data.Phone,
                    data.Email,
                    data.Address),
                actorUserId,
                cancellationToken);
            created = result.Company;

            await transaction.CommitAsync(cancellationToken);
        }

        // Después del commit, como en el alta por la API: un fallo de la siembra no deshace la empresa.
        await creation.SeedDefaultsAsync(created, actorUserId, cancellationToken);
        return created.Id.Value;
    }
}
