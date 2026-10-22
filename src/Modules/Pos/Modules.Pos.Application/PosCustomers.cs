using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

public sealed record SearchPosCustomersQuery(Guid TenantId, string? Search) : IQuery<PosCustomerOptionsResponse>;

public sealed record CreatePosCustomerCommand(
    Guid TenantId, string? Name, string? IdentificationType, string? IdentificationNumber, string? Phone)
    : ICommand<PosCustomerOptionResponse>;

/// <summary>
/// El documento ya es de otro cliente del tenant, activo o no. Es un 422 de dominio que además lleva a ese cliente
/// (<c>existing</c>): la caja lo ofrece en vez de dejar al cajero sin salida, o explica que está inactivo.
/// </summary>
public sealed class PosCustomerIdentificationTakenException(PosCustomerOptionResponse existing)
    : DomainException("pos.customer.identification_taken", "Another customer already uses that identification."),
      IHasProblemExtensions
{
    public IReadOnlyDictionary<string, object?> ProblemExtensions { get; } =
        new Dictionary<string, object?> { ["existing"] = existing };
}

// Públicos a propósito: AddValidatorsFromAssemblyContaining sólo registra los tipos públicos.
public sealed class SearchPosCustomersValidator : AbstractValidator<SearchPosCustomersQuery>
{
    public SearchPosCustomersValidator()
    {
        RuleFor(query => query.Search).MaximumLength(PosCustomerRules.SearchMaxLength);
    }
}

public sealed class CreatePosCustomerValidator : AbstractValidator<CreatePosCustomerCommand>
{
    // Los anchos de customers.customers (nombre 160, documento y teléfono 32), copiados porque Pos no ve Customers.
    private const int NameMaxLength = 160;

    private const int IdentificationNumberMaxLength = 32;

    private const int PhoneMaxLength = 32;

    public CreatePosCustomerValidator()
    {
        RuleFor(command => command.Name).NotEmpty().MaximumLength(NameMaxLength);
        RuleFor(command => command.IdentificationType)
            .Must(type => type is not null && PosCustomerRules.IdentificationTypes.Contains(type, StringComparer.Ordinal))
            .WithMessage("The identification type must be one of: " + string.Join(", ", PosCustomerRules.IdentificationTypes) + ".");
        RuleFor(command => command.IdentificationNumber).NotEmpty().MaximumLength(IdentificationNumberMaxLength);
        RuleFor(command => command.Phone).MaximumLength(PhoneMaxLength);
    }
}

public sealed class SearchPosCustomersHandler(
    IPosCustomerLookup customers,
    IExecutionContext executionContext,
    IValidator<SearchPosCustomersQuery> validator)
    : IQueryHandler<SearchPosCustomersQuery, PosCustomerOptionsResponse>
{
    public async Task<PosCustomerOptionsResponse> HandleAsync(
        SearchPosCustomersQuery query, CancellationToken cancellationToken)
    {
        PosAuthorization.EnsureAuthorized(executionContext, query.TenantId, PosPermissions.SaleCustomer);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var found = await customers.SearchAsync(query.TenantId, search, PosCustomerRules.Take, cancellationToken);
        return new PosCustomerOptionsResponse([.. found.Select(PosCustomerMapping.ToOption)]);
    }
}

public sealed class CreatePosCustomerHandler(
    IPosCustomerLookup customers,
    IExecutionContext executionContext,
    IValidator<CreatePosCustomerCommand> validator)
    : ICommandHandler<CreatePosCustomerCommand, PosCustomerOptionResponse>
{
    public async Task<PosCustomerOptionResponse> HandleAsync(
        CreatePosCustomerCommand command, CancellationToken cancellationToken)
    {
        // Autorizar antes de validar: un llamador ajeno no se lleva el mapa de errores.
        PosAuthorization.EnsureAuthorized(executionContext, command.TenantId, PosPermissions.SaleCustomer);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var type = command.IdentificationType!;
        var number = command.IdentificationNumber!.Trim();
        // Por legibilidad: la carrera entre dos cajas la resuelve la unidad de trabajo de Customers, que lanza el 422
        // customers.customer.identification_taken.
        if (await customers.FindByIdentificationAsync(command.TenantId, type, number, cancellationToken) is { } taken)
        {
            throw new PosCustomerIdentificationTakenException(PosCustomerMapping.ToOption(taken));
        }

        var phone = string.IsNullOrWhiteSpace(command.Phone) ? null : command.Phone.Trim();
        var created = await customers.CreateFromRegisterAsync(
            command.TenantId, new NewPosCustomer(command.Name!.Trim(), type, number, phone), cancellationToken);
        return PosCustomerMapping.ToOption(created);
    }
}

internal static class PosCustomerMapping
{
    public static PosCustomerOptionResponse ToOption(PosCustomerRef customer) =>
        new(customer.Id, customer.Name, customer.IdentificationType, customer.IdentificationNumber, customer.IsComplete, customer.IsActive);
}
