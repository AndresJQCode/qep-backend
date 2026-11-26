using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

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

/// <summary>
/// El alta rápida no trae el documento que el tenant exige al guardar un cliente, o lo trae a medias. Pos.Application no ve
/// Customers (PosLayerTests), así que repite sus códigos y las claves de <c>errors</c> de <c>CustomerFieldPolicyRequirement</c>:
/// el formulario de la caja marca los mismos campos que el del padrón. Es un 422 de dominio.
/// </summary>
public sealed class PosCustomerIdentificationException(string code, IReadOnlyDictionary<string, string[]> fieldErrors)
    : DomainException(code, "The customer identification is missing or incomplete."), IHasFieldErrors
{
    public const string Required = "customers.customer.identification_required";

    public const string Incomplete = "customers.customer.identification_incomplete";

    public const string NumberRequired = "customers.customer.identification_number_required";

    public IReadOnlyDictionary<string, string[]> FieldErrors { get; } = fieldErrors;
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
    public CreatePosCustomerValidator()
    {
        RuleFor(command => command.Name).NotEmpty().MaximumLength(PosCustomerRules.NameMaxLength);
        // El documento es opcional (spec 2026-11-09): que falte o venga a medias lo decide el handler, que conoce la
        // política del tenant. Acá sólo se mira la forma de lo que sí llegó.
        RuleFor(command => command.IdentificationType)
            .Must(type => PosCustomerRules.IdentificationTypes.Contains(type!, StringComparer.Ordinal))
            .WithMessage("The identification type must be one of: " + string.Join(", ", PosCustomerRules.IdentificationTypes) + ".")
            .When(command => !string.IsNullOrWhiteSpace(command.IdentificationType));
        RuleFor(command => command.IdentificationNumber).MaximumLength(PosCustomerRules.IdentificationNumberMaxLength);
        RuleFor(command => command.Phone).MaximumLength(PosCustomerRules.PhoneMaxLength);
    }
}

public sealed class SearchPosCustomersHandler(
    IPosCustomerLookup customers,
    ITenantDirectory tenantDirectory,
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
        // Spec 2026-11-09: si el tenant no exige el documento al facturar, la caja le vende a un incompleto sin documento
        // (su propia alta rápida), y el selector tiene que listarlo. Con la política por defecto, como antes.
        var regional = await tenantDirectory.GetRegionalSettingsAsync(new TenantId(query.TenantId), cancellationToken);
        var policy = regional?.EffectiveCustomerFieldPolicy ?? CustomerFieldPolicy.Default;
        var found = await customers.SearchAsync(
            query.TenantId, search, PosCustomerRules.Take, !policy.Document.OnBilling, cancellationToken);
        return new PosCustomerOptionsResponse([.. found.Select(PosCustomerMapping.ToOption)]);
    }
}

public sealed class CreatePosCustomerHandler(
    IPosCustomerLookup customers,
    ITenantDirectory tenantDirectory,
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

        var type = string.IsNullOrWhiteSpace(command.IdentificationType) ? null : command.IdentificationType;
        var number = string.IsNullOrWhiteSpace(command.IdentificationNumber) ? null : command.IdentificationNumber.Trim();
        await EnsureIdentificationAsync(command.TenantId, type, number, cancellationToken);

        // La consulta previa existe para devolver `existing` en el 422 (el selector lo ofrece en vez del alta); la
        // carrera entre dos cajas la cubre la unidad de trabajo de Customers, que lanza
        // customers.customer.identification_taken. Sin documento no hay a quién preguntarle.
        if (type is not null && number is not null
            && await customers.FindByIdentificationAsync(command.TenantId, type, number, cancellationToken) is { } taken)
        {
            throw new PosCustomerIdentificationTakenException(PosCustomerMapping.ToOption(taken));
        }

        var phone = string.IsNullOrWhiteSpace(command.Phone) ? null : command.Phone.Trim();
        var created = await customers.CreateFromRegisterAsync(
            command.TenantId, new NewPosCustomer(command.Name!.Trim(), type, number, phone), cancellationToken);
        return PosCustomerMapping.ToOption(created);
    }

    // Forma del par primero, sin leer el tenant; después la política (spec 2026-11-09 §6.4: el alta rápida es un
    // «guardar cliente», así que rige OnCustomerSave). Con documento la política no se lee. Los códigos y las claves
    // son los de CustomerFieldPolicyRequirement.
    private async Task EnsureIdentificationAsync(Guid tenantId, string? type, string? number, CancellationToken cancellationToken)
    {
        if (type is not null && number is null)
        {
            throw new PosCustomerIdentificationException(
                PosCustomerIdentificationException.NumberRequired,
                new Dictionary<string, string[]>
                {
                    ["IdentificationNumber"] = [PosCustomerIdentificationException.NumberRequired],
                });
        }

        if (number is not null && type is null)
        {
            throw new PosCustomerIdentificationException(
                PosCustomerIdentificationException.Incomplete,
                new Dictionary<string, string[]>
                {
                    ["IdentificationType"] = [PosCustomerIdentificationException.Incomplete],
                });
        }

        if (type is not null)
        {
            return;
        }

        var regional = await tenantDirectory.GetRegionalSettingsAsync(new TenantId(tenantId), cancellationToken);
        var policy = regional?.EffectiveCustomerFieldPolicy ?? CustomerFieldPolicy.Default;
        if (policy.Document.OnCustomerSave)
        {
            throw new PosCustomerIdentificationException(
                PosCustomerIdentificationException.Required,
                new Dictionary<string, string[]>
                {
                    ["IdentificationType"] = [PosCustomerIdentificationException.Required],
                    ["IdentificationNumber"] = [PosCustomerIdentificationException.Required],
                });
        }
    }
}

internal static class PosCustomerMapping
{
    public static PosCustomerOptionResponse ToOption(PosCustomerRef customer) =>
        new(customer.Id, customer.Name, customer.IdentificationType, customer.IdentificationNumber, customer.IsComplete, customer.IsActive);
}
