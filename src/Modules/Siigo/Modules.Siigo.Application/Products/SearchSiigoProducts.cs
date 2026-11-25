using System.Globalization;
using System.Text;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Siigo.Application.Settings;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;

namespace Modules.Siigo.Application.Products;

/// <summary>Un producto de Siigo que se puede elegir para vincular una variante: lo que el selector del paso 6 muestra en cada fila.</summary>
public sealed record SiigoProductCandidateDto(string Id, string Code, string Name);

/// <summary>Una página de productos activos de Siigo. <c>Total</c> cuenta todo lo que casa con la búsqueda, no sólo la página.</summary>
public sealed record SiigoProductCandidatesPageDto(IReadOnlyList<SiigoProductCandidateDto> Items, int Page, int PageSize, int Total);

/// <summary>La búsqueda y la paginación del selector. Pura: la lista completa ya viene de <see cref="ISiigoApi.ListActiveProductsAsync"/>.</summary>
public static class SiigoProductPicker
{
    /// <summary>De a cuántas filas pagina el selector. Fijo: la pantalla no lo elige.</summary>
    public const int PageSize = 20;

    /// <summary>La última página cuyo desplazamiento cabe en un <c>int</c>. Una mayor se recorta a ésta: sin el tope el producto se
    /// desborda y vuelve la primera página.</summary>
    public const int MaxPage = int.MaxValue / PageSize;

    /// <summary>Casa por código o por nombre, sin distinguir mayúsculas ni tildes («pantalon» encuentra «Pantalón»), y ordena por código.</summary>
    public static SiigoProductCandidatesPageDto Page(IReadOnlyList<SiigoProductOption> products, string? search, int page)
    {
        ArgumentNullException.ThrowIfNull(products);
        var term = Fold(search);
        var matches = products
            .Where(product => term.Length == 0 || Fold(product.Code).Contains(term, StringComparison.Ordinal)
                || Fold(product.Name).Contains(term, StringComparison.Ordinal))
            .OrderBy(product => product.Code, StringComparer.OrdinalIgnoreCase)
            .ThenBy(product => product.Id, StringComparer.Ordinal)
            .ToList();
        var current = Math.Clamp(page, 1, MaxPage);
        return new SiigoProductCandidatesPageDto(
            [.. matches.Skip((current - 1) * PageSize).Take(PageSize).Select(product => new SiigoProductCandidateDto(product.Id, product.Code, product.Name))],
            current,
            PageSize,
            matches.Count);
    }

    private static string Fold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC).ToUpperInvariant();
    }
}

/// <summary><c>GET …/siigo-products?search=&amp;page=&amp;refresh=</c> (<c>siigo.settings.manage</c>).</summary>
public sealed record SearchSiigoProductsQuery(Guid TenantId, Guid ConnectionId, string? Search, int? Page, bool Refresh)
    : IQuery<SiigoProductCandidatesPageDto>;

/// <summary>La forma de la consulta: <c>page</c> de 1 en adelante (422 <c>validation.failed</c> con el campo).</summary>
public sealed class SearchSiigoProductsValidator : AbstractValidator<SearchSiigoProductsQuery>
{
    public SearchSiigoProductsValidator()
    {
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1).When(query => query.Page is not null).OverridePropertyName("page");
        RuleFor(query => query.Search).MaximumLength(200).OverridePropertyName("search");
    }
}

/// <summary>
/// El selector del paso 6: los productos activos que Siigo tiene hoy, para elegir con cuál se vincula una variante en vez de teclear su
/// id, código y nombre. En orden: tenant, permiso y módulo (403); la forma (422); la conexión del tenant (403); las credenciales (una
/// conexión que no se puede usar es 409 <c>siigo.connection_unavailable</c>). Un Siigo que no contesta es 503 <c>siigo.unavailable</c>.
/// <c>refresh=true</c> se salta la copia de 60 s: la pantalla lo manda al abrir el selector para ver lo que se creó en Siigo hace un momento.
/// </summary>
public sealed class SearchSiigoProductsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISiigoConnectionDirectory connections,
    ISiigoApi api,
    IValidator<SearchSiigoProductsQuery> validator) : IQueryHandler<SearchSiigoProductsQuery, SiigoProductCandidatesPageDto>
{
    public async Task<SiigoProductCandidatesPageDto> HandleAsync(SearchSiigoProductsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await SiigoSettingsSupport.AuthorizeAsync(executionContext, tenantModules, query.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);
        await SiigoSettingsSupport.ConnectionAsync(connections, query.TenantId, query.ConnectionId, cancellationToken);

        var resolution = await connections.ResolveForSetupAsync(query.TenantId, query.ConnectionId, cancellationToken);
        if (resolution.Credentials is not { } credentials)
        {
            throw new ResourceConflictException(
                SiigoErrorCodes.ConnectionUnavailable,
                $"The Siigo connection cannot be used right now ({resolution.UnavailableReason}).");
        }

        var products = await api.ListActiveProductsAsync(credentials, query.Refresh, cancellationToken);
        return SiigoProductPicker.Page(products, query.Search, query.Page ?? 1);
    }
}
