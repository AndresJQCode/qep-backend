using Microsoft.EntityFrameworkCore;
using Modules.Companies.Application;
using Modules.Companies.Domain;

namespace Modules.Companies.Infrastructure.Persistence;

internal sealed class CompanyRepository(CompaniesDbContext dbContext) : ICompanyRepository
{
    private const string LikeEscapeCharacter = "\\";

    // La barra va primero: escaparla despues convertiria en literal la barra que acaban de
    // agregar los otros dos reemplazos.
    private static string EscapeLikeWildcards(string term) => term
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    public async Task<IReadOnlyList<Company>> SearchAsync(
        Guid tenantId,
        string? search,
        CompanyStatusFilter? status,
        CancellationToken cancellationToken)
    {
        var query = dbContext.Companies
            .AsNoTracking()
            .Where(company => company.TenantId == tenantId);

        if (status is not null)
        {
            var isActive = status == CompanyStatusFilter.Active;
            query = query.Where(company => company.IsActive == isActive);
        }

        var term = search?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            // ILike es la coincidencia case-insensitive de Npgsql. Los comodines tienen que ser
            // solo los dos que pone esta linea: `%` y `_` son comodines de LIKE, asi que un
            // termino sin escapar los convierte en parte de la sintaxis. `?search=_` devolvia el
            // catalogo entero —coincide con cualquier caracter—, que es lo contrario de filtrar.
            // Lo encontro la revision de fiabilidad de CAT-02, sobre este mismo codigo.
            //
            // Busca por nombre y por numero de cuenta. El NIT no entra: nadie lo escribe de memoria
            // en un buscador, y agregarlo despues no rompe a nadie.
            //
            // Desde EMP-08 el numero vive en la coleccion, asi que la condicion pasa a ser "alguna
            // de sus cuentas coincide" y EF la traduce a un EXISTS sobre company_bank_accounts.
            // Coincidir en una sola alcanza: quien escribe un numero en el buscador quiere la
            // empresa que lo tiene.
            //
            // El nombre del banco no entra todavia. Es defendible que entre —"todas las de
            // Bancolombia" es una busqueda razonable—, pero cambia lo que el usuario espera de la
            // caja y eso lo decide el gate del modulo, no este slice.
            var pattern = $"%{EscapeLikeWildcards(term)}%";
            query = query.Where(company =>
                EF.Functions.ILike(company.Name, pattern, LikeEscapeCharacter) ||
                company.BankAccounts.Any(account =>
                    EF.Functions.ILike(account.AccountNumber, pattern, LikeEscapeCharacter)));
        }

        return await query
            .OrderBy(company => company.Name)
            .ToListAsync(cancellationToken);
    }

    // Con tracking a proposito, a diferencia de SearchAsync: los llamadores de este mutan el
    // agregado y dependen de la unidad de trabajo para persistirlo.
    public Task<Company?> FindAsync(
        Guid tenantId,
        CompanyId companyId,
        CancellationToken cancellationToken) =>
        dbContext.Companies.SingleOrDefaultAsync(
            company => company.TenantId == tenantId && company.Id == companyId,
            cancellationToken);

    public void Add(Company company) => dbContext.Companies.Add(company);

    // Las cuentas bancarias se van con la empresa sin que este método las toque: son una colección
    // owned, y EF emite el DELETE de las filas hijas antes que el del padre.
    public void Remove(Company company) => dbContext.Companies.Remove(company);
}
