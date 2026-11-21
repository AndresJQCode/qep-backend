using FluentValidation;
using Modules.Hr.Domain;

namespace Modules.Hr.Application;

internal static class HrSettingsRules
{
    public const int NameMaxLength = 100;

    public static IRuleBuilderOptions<T, string?> MustBeASettingName<T>(this IRuleBuilder<T, string?> rule) => rule
        .Must(name => !string.IsNullOrWhiteSpace(name)).WithMessage("Escribe un nombre.")
        .Must(name => name is null || name.Trim().Length <= NameMaxLength)
        .WithMessage($"El nombre no puede pasar de {NameMaxLength} caracteres.");
}

// Los validadores son públicos a propósito: AddValidatorsFromAssembly sólo registra tipos públicos, y uno que no se registra deja
// pasar el cuerpo sin validar.
public sealed class SiteRequestValidator : HrValidator<SiteRequest>
{
    public SiteRequestValidator()
    {
        RuleFor(request => request.CompanyId)
            .Must(id => id is { } value && value != Guid.Empty).WithMessage("Elige una empresa.");
        RuleFor(request => request.Name).MustBeASettingName();
        RuleFor(request => request.Address)
            .Must(address => address is null || address.Trim().Length <= 200)
            .WithMessage("La dirección no puede pasar de 200 caracteres.");
        // La caja no se valida acá: depende del país de la empresa (HrSiteCompensationFund, en los handlers).
    }
}

public sealed class WorkplaceRequestValidator : HrValidator<WorkplaceRequest>
{
    public WorkplaceRequestValidator()
    {
        RuleFor(request => request.Name).MustBeASettingName();
        RuleFor(request => request.SiteId)
            .Must(id => id is { } value && value != Guid.Empty).WithMessage("Elige una sede.");
        RuleFor(request => request.CiiuCode)
            .Matches("^[0-9]{4}$").WithMessage("El código CIIU tiene cuatro dígitos.")
            .When(request => !string.IsNullOrWhiteSpace(request.CiiuCode));
        RuleFor(request => request.RiskClass)
            .Must(riskClass => HrEnums.TryParse<ArlRiskClass>(riskClass, out _))
            .WithMessage("Elige una clase de riesgo válida (I, II, III, IV o V).");
    }
}

public sealed class NamedSettingRequestValidator : HrValidator<NamedSettingRequest>
{
    public NamedSettingRequestValidator() => RuleFor(request => request.Name).MustBeASettingName();
}
