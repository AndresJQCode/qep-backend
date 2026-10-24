using BuildingBlocks.Application;
using FluentValidation;
using Modules.Customers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Customers.Application;

/// <summary>
/// Spec 2026-10-24 §3.2: el número de documento sin signos ni mayúsculas, que es con lo que se comparan dos clientes.
/// La misma regla que la expresión SQL del índice <c>IX_customers_tenant_identification_normalized</c>
/// (<c>CustomerIdentificationSql.NormalizedNumber</c>): sólo letras y dígitos ASCII, en mayúsculas.
/// </summary>
public static class CustomerIdentificationNormalizer
{
    public static string Normalize(string? number)
    {
        if (string.IsNullOrEmpty(number))
        {
            return string.Empty;
        }

        Span<char> buffer = number.Length <= 128 ? stackalloc char[number.Length] : new char[number.Length];
        var length = 0;
        foreach (var character in number)
        {
            if (character is >= '0' and <= '9' or >= 'A' and <= 'Z')
            {
                buffer[length++] = character;
            }
            else if (character is >= 'a' and <= 'z')
            {
                buffer[length++] = (char)(character - 32);
            }
        }

        return new string(buffer[..length]);
    }
}

/// <summary>
/// Spec 2026-10-24 §3.2: cómo se parece un cliente existente al documento que se está escribiendo. Viaja con su nombre
/// (<c>SameIdentification</c>) porque el diccionario de colores lo tiene el frontend.
/// </summary>
public enum CustomerIdentificationMatchKind
{
    /// <summary>Mismo tipo y mismo número tal cual se escribió: el índice único también lo frenaría.</summary>
    SameIdentification,

    /// <summary>El mismo número con otro tipo de documento (o sin tipo en la consulta).</summary>
    OtherType,

    /// <summary>El mismo tipo y el mismo número salvo puntos, guiones o mayúsculas.</summary>
    SameTypeOtherFormat
}

public static class CustomerIdentificationMatching
{
    /// <summary>Tope de coincidencias: es un aviso, no un listado.</summary>
    public const int Take = 20;
}

/// <param name="Number">Texto libre del formulario; se normaliza en el handler.</param>
/// <param name="Type">Opcional. Sin tipo, toda coincidencia sale como <c>OtherType</c>.</param>
/// <param name="ExcludeCustomerId">El cliente que se está editando, para que no se avise de sí mismo.</param>
public sealed record FindCustomerIdentificationMatchesQuery(Guid TenantId, string? Number, string? Type, Guid? ExcludeCustomerId)
    : IQuery<CustomerIdentificationMatchesResponse>;

/// <summary>
/// <c>number</c> vacío o de más de 32 caracteres da <c>errors.Number</c>, y un <c>type</c> que el producto no acepta da
/// <c>errors.Type</c>: el mapa que el formulario sabe leer para marcar el input (y no un 400 del binding o un
/// <c>CustomersDomainException</c> del parser).
/// </summary>
public sealed class FindCustomerIdentificationMatchesValidator : AbstractValidator<FindCustomerIdentificationMatchesQuery>
{
    public FindCustomerIdentificationMatchesValidator()
    {
        RuleFor(query => query.Number)
            .Must(number => !string.IsNullOrWhiteSpace(number))
            .WithMessage("Escribe el número de documento.")
            .Must(number => number is null || number.Trim().Length <= CustomerIdentification.NumberMaxLength)
            .WithMessage($"El número de documento no puede pasar de {CustomerIdentification.NumberMaxLength} caracteres.");

        RuleFor(query => query.Type)
            .Must(type => IdentificationTypeParser.SupportedWireValues.Contains(type!.Trim(), StringComparer.OrdinalIgnoreCase))
            .When(query => query.Type is not null)
            .WithMessage($"El tipo de documento debe ser uno de {string.Join(", ", IdentificationTypeParser.SupportedWireValues)}.");
    }
}

public sealed class FindCustomerIdentificationMatchesHandler(
    ICustomerDuplicateQueries queries,
    IValidator<FindCustomerIdentificationMatchesQuery> validator,
    IExecutionContext executionContext)
    : IQueryHandler<FindCustomerIdentificationMatchesQuery, CustomerIdentificationMatchesResponse>
{
    public async Task<CustomerIdentificationMatchesResponse> HandleAsync(
        FindCustomerIdentificationMatchesQuery query, CancellationToken cancellationToken)
    {
        CustomersAuthorization.EnsureAuthorized(executionContext, query.TenantId, CustomersPermissions.CustomerRead);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        // Un número de puros signos ("---") normaliza a vacío: compararlo coincidiría con todo cliente cuyo número también
        // lo hiciera. No es una coincidencia, es la ausencia de dato.
        var normalized = CustomerIdentificationNormalizer.Normalize(query.Number);
        if (normalized.Length == 0)
        {
            return new CustomerIdentificationMatchesResponse([]);
        }

        IdentificationType? type = query.Type is null ? null : IdentificationTypeParser.Parse(query.Type);
        var typed = query.Number!.Trim();
        var customers = await queries.FindByNormalizedIdentificationAsync(
            query.TenantId, normalized, query.ExcludeCustomerId, CustomerIdentificationMatching.Take, cancellationToken);

        var items = customers
            .Select(customer => new CustomerIdentificationMatchDto(
                customer.Id.Value,
                customer.Name,
                customer.IdentificationType!.Value.ToWireValue(),
                customer.IdentificationNumber!,
                customer.Cuc,
                customer.IsComplete,
                customer.IsActive,
                Classify(customer, type, typed).ToString()))
            .ToArray();

        return new CustomerIdentificationMatchesResponse(items);
    }

    private static CustomerIdentificationMatchKind Classify(Customer customer, IdentificationType? type, string typed) =>
        type is null || customer.IdentificationType != type
            ? CustomerIdentificationMatchKind.OtherType
            : string.Equals(customer.IdentificationNumber, typed, StringComparison.Ordinal)
                ? CustomerIdentificationMatchKind.SameIdentification
                : CustomerIdentificationMatchKind.SameTypeOtherFormat;
}

/// <summary>Spec 2026-10-24 §3.3: por qué se agrupan dos clientes. Viaja con su nombre (<c>SharedPhone</c>).</summary>
public enum CustomerAnomalyKind
{
    /// <summary>El mismo celular (<c>phone_e164</c>).</summary>
    SharedPhone,

    /// <summary>El mismo número de documento, sin importar tipo, puntos ni guiones.</summary>
    SharedIdentificationNumber
}

/// <summary>
/// Un grupo pesa hasta 50 clientes (spec §3.3), así que la lista no reutiliza <see cref="CustomerPaging"/>: pagina grupos y
/// tiene sus propios topes.
/// </summary>
public static class CustomerAnomalyPaging
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;

    /// <summary>Clientes que viajan por grupo; el que sobra sólo sirve para marcar <c>truncated</c>.</summary>
    public const int GroupCustomersCap = 50;
}

/// <param name="Kind">Nombre de <see cref="CustomerAnomalyKind"/>; obligatorio.</param>
/// <param name="Page">Menor que 1 se recorta a 1.</param>
/// <param name="PageSize">Menor que 1 vale el default y mayor que el máximo se recorta, en silencio, como el resto de listas.</param>
public sealed record ListCustomerAnomaliesQuery(Guid TenantId, string? Kind, int Page, int PageSize)
    : IQuery<CustomerAnomaliesResponse>;

/// <summary><c>kind</c> ausente o que no es un nombre del enum da <c>errors.Kind</c>: el mapa que el formulario sabe leer.</summary>
public sealed class ListCustomerAnomaliesValidator : AbstractValidator<ListCustomerAnomaliesQuery>
{
    public ListCustomerAnomaliesValidator()
    {
        var names = Enum.GetNames<CustomerAnomalyKind>();
        RuleFor(query => query.Kind)
            .Must(kind => kind is not null && names.Contains(kind, StringComparer.Ordinal))
            .WithMessage($"La clase de anomalía debe ser una de {string.Join(", ", names)}.");
    }
}

public sealed class ListCustomerAnomaliesHandler(
    ICustomerDuplicateQueries queries,
    IValidator<ListCustomerAnomaliesQuery> validator,
    IExecutionContext executionContext)
    : IQueryHandler<ListCustomerAnomaliesQuery, CustomerAnomaliesResponse>
{
    public async Task<CustomerAnomaliesResponse> HandleAsync(ListCustomerAnomaliesQuery query, CancellationToken cancellationToken)
    {
        CustomersAuthorization.EnsureAuthorized(executionContext, query.TenantId, CustomersPermissions.CustomerRead);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var kind = Enum.Parse<CustomerAnomalyKind>(query.Kind!);
        var page = Math.Max(query.Page, 1);
        var pageSize = query.PageSize < 1
            ? CustomerAnomalyPaging.DefaultPageSize
            : Math.Min(query.PageSize, CustomerAnomalyPaging.MaxPageSize);

        var found = await queries.CountAnomalyGroupsAsync(query.TenantId, cancellationToken);
        var counts = Enum.GetValues<CustomerAnomalyKind>()
            .Select(each => new CustomerAnomalyCountDto(each.ToString(), found.GetValueOrDefault(each)))
            .ToArray();

        var rows = await queries.ListAnomalyGroupsAsync(query.TenantId, kind, (page - 1) * pageSize, pageSize, cancellationToken);
        var members = rows.Count == 0
            ? new Dictionary<string, IReadOnlyList<Customer>>()
            : await queries.ListAnomalyGroupMembersAsync(
                query.TenantId, kind, rows.Select(row => row.Key).ToArray(), CustomerAnomalyPaging.GroupCustomersCap, cancellationToken);

        var groups = rows
            .Select(row =>
            {
                var customers = members.TryGetValue(row.Key, out var list) ? list : [];
                return new CustomerAnomalyGroupDto(
                    row.Key,
                    customers.Take(CustomerAnomalyPaging.GroupCustomersCap).Select(ToDto).ToArray(),
                    customers.Count > CustomerAnomalyPaging.GroupCustomersCap);
            })
            .ToArray();

        return new CustomerAnomaliesResponse(counts, groups, found.GetValueOrDefault(kind), page, pageSize);
    }

    private static CustomerAnomalyCustomerDto ToDto(Customer customer) =>
        new(
            customer.Id.Value,
            customer.Name,
            customer.IdentificationType?.ToWireValue(),
            customer.IdentificationNumber,
            customer.Cuc,
            customer.Phone,
            customer.IsComplete,
            customer.IsActive,
            customer.CreatedAt);
}
