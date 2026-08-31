using Modules.Companies.Domain;

namespace Modules.Companies.Application;

internal static class CompanyMapping
{
    /// <summary>
    /// Del agregado al DTO, con la ciudad ya resuelta por el llamador. No la resuelve esta
    /// funcion: cada handler decide como (una consulta puntual en Get/Create/Update, un lote en
    /// List) y esto solo ensambla — mismo criterio que <c>CustomerMapping.ToDto</c>.
    /// </summary>
    public static CompanyDto ToDto(this Company company, CompanyCityRef city) => new(
        company.Id.Value,
        company.Name,
        company.BankAccounts
            .Select(account => new CompanyBankAccountPayload(
                account.BankName,
                account.AccountNumber,
                account.Currency))
            .ToArray(),
        company.TaxId,
        company.IsActive,
        company.Phone,
        company.Email,
        company.Address,
        new CompanyCityDto(city.CityId, city.CityDivipolaCode, city.CityName),
        new CompanyDepartmentDto(
            city.DepartmentId, city.DepartmentDivipolaCode, city.DepartmentName),
        company.CreatedAt,
        company.UpdatedAt);

    /// <summary>
    /// La version de una sola empresa: resuelve su ciudad y arma el DTO. Para
    /// <c>GetCompanyHandler</c>, <c>DeactivateCompanyHandler</c> y <c>ActivateCompanyHandler</c>,
    /// que ya tienen la <c>Company</c> en mano y no necesitan resolver nada mas antes.
    ///
    /// La FK de base garantiza que la ciudad exista, asi que un miss aca es corrupcion de datos
    /// y no una entrada de usuario invalida — por eso lanza <see cref="InvalidOperationException"/>
    /// (500) y no un <see cref="CompaniesDomainException"/> (422): no hay ningun campo del
    /// request que el llamador pueda corregir.
    /// </summary>
    public static async Task<CompanyDto> ToDtoAsync(
        this Company company,
        ICompanyGeographyLookup geographyLookup,
        CancellationToken cancellationToken)
    {
        var city = await geographyLookup.FindCityAsync(company.CityId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"City '{company.CityId}' referenced by company '{company.Id}' was not found.");

        return company.ToDto(city);
    }

    /// <summary>
    /// Del contrato HTTP al dominio. No normaliza ni valida nada: eso es trabajo de
    /// <see cref="CompanyBankAccount.Normalized"/>, y hacerlo tambien aca crearia una segunda
    /// definicion de "cuenta valida" que el dia que difiera nadie va a notar.
    ///
    /// El <c>?? []</c> cubre la lista ausente del JSON. El validador ya la rechaza con su mapa de
    /// errores por campo y corre antes que esto, asi que en la practica no llega; esta igual para
    /// que un llamador futuro que se saltee el validador falle como 422 del dominio y no como
    /// NullReferenceException.
    /// </summary>
    public static IReadOnlyCollection<CompanyBankAccount> ToDomain(
        this IReadOnlyList<CompanyBankAccountPayload>? payloads) =>
        (payloads ?? [])
            .Select(payload => new CompanyBankAccount
            {
                BankName = payload.BankName,
                AccountNumber = payload.AccountNumber,
                Currency = payload.Currency
            })
            .ToArray();
}
