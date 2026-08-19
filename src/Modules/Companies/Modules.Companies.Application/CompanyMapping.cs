using Modules.Companies.Domain;

namespace Modules.Companies.Application;

internal static class CompanyMapping
{
    public static CompanyDto ToDto(this Company company) => new(
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
        company.CreatedAt,
        company.UpdatedAt);

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
