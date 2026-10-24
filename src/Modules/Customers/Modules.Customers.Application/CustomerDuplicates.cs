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
