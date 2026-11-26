using Modules.Hr.Domain;

namespace Modules.Hr.Application;

/// <summary>La empresa de Companies como la ve la pantalla: razón social, NIT, país (ISO alfa-2) y si sigue activa.</summary>
public sealed record CompanyRefDto(Guid Id, string Name, string TaxId, bool IsActive, string Country);

/// <summary>
/// Lo que Hr hace con las empresas de Companies, sobre el puerto <see cref="IHrCompanyLookup"/>. Una empresa con RRHH no se borra
/// (FK, D-11), así que una que no existe al resolver es un error de programación y no un caso.
/// </summary>
public sealed class HrCompanyRefs(IHrCompanyLookup companies)
{
    public async Task<IReadOnlyList<CompanyRefDto>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken) =>
        (await companies.ListActiveAsync(tenantId, cancellationToken))
            .Select(ToDto)
            .OrderBy(company => company.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(company => company.Id)
            .ToList();

    /// <summary>Las empresas de los ids pedidos, activas o no: una desactivada después sigue en sus sedes y contratos (D-12).</summary>
    public async Task<IReadOnlyDictionary<Guid, CompanyRefDto>> ResolveAsync(
        Guid tenantId, IEnumerable<Guid> companyIds, CancellationToken cancellationToken)
    {
        var active = (await companies.ListActiveAsync(tenantId, cancellationToken)).ToDictionary(company => company.Id);
        var resolved = new Dictionary<Guid, CompanyRefDto>();
        foreach (var id in companyIds.Distinct())
        {
            var company = active.GetValueOrDefault(id)
                ?? await companies.FindAsync(tenantId, id, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Company '{id}' does not exist, but an hr record references it (the foreign key should make that impossible).");
            resolved[id] = ToDto(company);
        }

        return resolved;
    }

    /// <summary>
    /// Los vínculos nuevos (D-12): crear una sede, el alta, la reincorporación y la importación exigen una empresa activa. La
    /// conversión de tipo no lo llama: es el mismo empleador.
    /// </summary>
    public async Task<HrCompanyRef> RequireActiveAsync(
        Guid tenantId, Guid companyId, string field, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(tenantId, companyId, cancellationToken);
        if (company is not { IsActive: true })
        {
            HrFieldErrors.Throw(field, "Elige una empresa activa.");
        }

        return company;
    }

    private static CompanyRefDto ToDto(HrCompanyRef company) => new(company.Id, company.Name, company.TaxId, company.IsActive, company.Country);
}
