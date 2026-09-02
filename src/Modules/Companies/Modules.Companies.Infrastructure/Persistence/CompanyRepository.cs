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

    // `null` para un filtro vacio/ausente, para que el llamador sepa si tiene que agregar el
    // `Where` o no — mismo patron que `CustomerRepository.LikePattern`.
    private static string? LikePattern(string? term)
    {
        var trimmed = term?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : $"%{EscapeLikeWildcards(trimmed)}%";
    }

    public async Task<IReadOnlyList<Company>> SearchAsync(
        Guid tenantId,
        string? search,
        string? name,
        string? taxId,
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

        // ILike es la coincidencia case-insensitive de Npgsql. Los comodines tienen que ser
        // solo los dos que pone `LikePattern`: `%` y `_` son comodines de LIKE, asi que un
        // termino sin escapar los convierte en parte de la sintaxis. `?search=_` devolvia el
        // catalogo entero —coincide con cualquier caracter—, que es lo contrario de filtrar.
        // Lo encontro la revision de fiabilidad de CAT-02, sobre este mismo codigo.
        //
        // Busca por nombre y por numero de cuenta. El NIT no entra aca: es el criterio OR
        // original, y agregarlo lo haria menos preciso — para eso esta `taxId`, mas abajo, como
        // caja propia (CLI-FILTROS-01).
        //
        // Desde EMP-08 el numero vive en la coleccion, asi que la condicion pasa a ser "alguna
        // de sus cuentas coincide" y EF la traduce a un EXISTS sobre company_bank_accounts.
        // Coincidir en una sola alcanza: quien escribe un numero en el buscador quiere la
        // empresa que lo tiene.
        //
        // El nombre del banco no entra todavia. Es defendible que entre —"todas las de
        // Bancolombia" es una busqueda razonable—, pero cambia lo que el usuario espera de la
        // caja y eso lo decide el gate del modulo, no este slice.
        var searchPattern = LikePattern(search);
        if (searchPattern is not null)
        {
            query = query.Where(company =>
                EF.Functions.ILike(company.Name, searchPattern, LikeEscapeCharacter) ||
                company.BankAccounts.Any(account =>
                    EF.Functions.ILike(account.AccountNumber, searchPattern, LikeEscapeCharacter)));
        }

        // Dos cajas separadas (CLI-FILTROS-01), cada una filtra su propia columna y se combinan
        // con AND cuando el llamador manda mas de una — mismo patron que
        // `CustomerRepository.SearchAsync`.
        var namePattern = LikePattern(name);
        if (namePattern is not null)
        {
            query = query.Where(company =>
                EF.Functions.ILike(company.Name, namePattern, LikeEscapeCharacter));
        }

        var taxIdPattern = LikePattern(taxId);
        if (taxIdPattern is not null)
        {
            query = query.Where(company =>
                EF.Functions.ILike(company.TaxId, taxIdPattern, LikeEscapeCharacter));
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
