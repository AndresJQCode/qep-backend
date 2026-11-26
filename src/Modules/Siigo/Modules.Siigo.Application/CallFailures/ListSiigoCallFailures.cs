using System.Globalization;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;

namespace Modules.Siigo.Application.CallFailures;

/// <summary>
/// <c>GET …/siigo/call-failures?page&amp;pageSize&amp;operation&amp;status&amp;from&amp;to</c> (<c>siigo.calls.read</c>). Todos los filtros llegan como
/// texto y los valida <see cref="ListSiigoCallFailuresValidator"/>: un valor que no se puede leer es 422 <c>validation.failed</c> con el
/// mapa <c>errors</c> y el campo, que es el único 422 que el formulario sabe marcar; un parámetro tipado daría un 400 de enlace sin campo.
/// <c>Operation</c> es el nombre de una <see cref="SiigoCallOperation"/>.
/// </summary>
public sealed record ListSiigoCallFailuresQuery(
    Guid TenantId, string? Operation, string? Status, string? From, string? To, string? Page, string? PageSize)
    : IQuery<SiigoCallFailurePageDto>;

/// <summary>La paginación de las listas de Siigo: las fallas y las tandas. Un valor fuera de rango se recorta, y la respuesta devuelve el
/// que se usó.</summary>
public static class SiigoCallFailurePaging
{
    public const int DefaultPageSize = 25;

    public const int MaxPageSize = 100;

    /// <summary>La última página cuyo desplazamiento (<c>(page - 1) * pageSize</c>) cabe en un <c>int</c> con el tamaño máximo. Una mayor
    /// se recorta a ésta: sin el tope el producto se desborda y la consulta sale con un desplazamiento negativo (500).</summary>
    public const int MaxPage = int.MaxValue / MaxPageSize;

    public static int NormalizePage(int page) => Math.Clamp(page, 1, MaxPage);

    public static int NormalizePageSize(int pageSize) => pageSize switch
    {
        < 1 => DefaultPageSize,
        > MaxPageSize => MaxPageSize,
        _ => pageSize,
    };
}

public sealed class ListSiigoCallFailuresValidator : AbstractValidator<ListSiigoCallFailuresQuery>
{
    public ListSiigoCallFailuresValidator()
    {
        // Sólo el nombre: Enum.TryParse también acepta «3» y «Auth,ListRead», que filtrarían por otra operación sin avisar.
        RuleFor(query => query.Operation)
            .Must(operation => Enum.GetNames<SiigoCallOperation>().Contains(operation!.Trim(), StringComparer.OrdinalIgnoreCase))
            .When(query => !string.IsNullOrWhiteSpace(query.Operation))
            .WithMessage("The operation is not one of the known Siigo operations.")
            .OverridePropertyName("operation");
        RuleFor(query => query.Status)
            .Must(status => ListSiigoCallFailuresQueryParser.TryInt(status, out var code) && code is >= 100 and <= 599)
            .When(query => !string.IsNullOrWhiteSpace(query.Status))
            .WithMessage("The status must be an HTTP status code between 100 and 599.")
            .OverridePropertyName("status");
        RuleFor(query => query.From)
            .Must(value => ListSiigoCallFailuresQueryParser.TryDate(value, out _))
            .When(query => !string.IsNullOrWhiteSpace(query.From))
            .WithMessage("The from date must be an ISO 8601 date or date and time.")
            .OverridePropertyName("from");
        RuleFor(query => query.To)
            .Must(value => ListSiigoCallFailuresQueryParser.TryDate(value, out _))
            .When(query => !string.IsNullOrWhiteSpace(query.To))
            .WithMessage("The to date must be an ISO 8601 date or date and time.")
            .OverridePropertyName("to");
        RuleFor(query => query.Page)
            .Must(value => ListSiigoCallFailuresQueryParser.TryInt(value, out _))
            .When(query => !string.IsNullOrWhiteSpace(query.Page))
            .WithMessage("The page must be a whole number.")
            .OverridePropertyName("page");
        RuleFor(query => query.PageSize)
            .Must(value => ListSiigoCallFailuresQueryParser.TryInt(value, out _))
            .When(query => !string.IsNullOrWhiteSpace(query.PageSize))
            .WithMessage("The page size must be a whole number.")
            .OverridePropertyName("pageSize");
    }
}

/// <summary>La lectura de los filtros de texto, la misma en el validador y en el handler. Un instante sin offset (<c>2026-11-21</c>) es UTC.</summary>
internal static class ListSiigoCallFailuresQueryParser
{
    public static bool TryInt(string? value, out int result) =>
        int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    public static bool TryDate(string? value, out DateTimeOffset result) =>
        DateTimeOffset.TryParse(value?.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out result);
}

/// <summary>
/// Spec 2026-11-21. En orden: tenant, permiso y módulo (403, nunca 404); la forma (422). Sin curl ni respuesta en la lista: ver
/// <see cref="SiigoCallFailureSummaryDto"/>.
/// </summary>
public sealed class ListSiigoCallFailuresHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISiigoCallFailureRepository repository,
    IValidator<ListSiigoCallFailuresQuery> validator) : IQueryHandler<ListSiigoCallFailuresQuery, SiigoCallFailurePageDto>
{
    public async Task<SiigoCallFailurePageDto> HandleAsync(ListSiigoCallFailuresQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await SiigoAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, SiigoPermissions.CallsRead, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var page = SiigoCallFailurePaging.NormalizePage(Int(query.Page) ?? 1);
        var pageSize = SiigoCallFailurePaging.NormalizePageSize(Int(query.PageSize) ?? 0);
        SiigoCallOperation? operation = string.IsNullOrWhiteSpace(query.Operation)
            ? null
            : Enum.Parse<SiigoCallOperation>(query.Operation.Trim(), ignoreCase: true);

        var (items, total) = await repository.SearchAsync(
            query.TenantId, operation, Int(query.Status), Date(query.From), Date(query.To), page, pageSize, cancellationToken);
        return new SiigoCallFailurePageDto([.. items.Select(SiigoCallFailureSummaryDto.From)], total, page, pageSize);
    }

    private static int? Int(string? value) => ListSiigoCallFailuresQueryParser.TryInt(value, out var parsed) ? parsed : null;

    private static DateTimeOffset? Date(string? value) => ListSiigoCallFailuresQueryParser.TryDate(value, out var parsed) ? parsed : null;
}
