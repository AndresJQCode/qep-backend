using FluentValidation;

namespace Modules.Buyers.Application;

/// <summary>
/// El perfil guardado precarga el formulario del pedido, así que sus reglas son las de
/// <c>PublicOrderRequestBodyValidator</c> (Billing y Shipping) de Store, copiadas porque Buyers no ve Store y aquellos validadores
/// son privados. <c>BuyerParityTests</c> (Bootstrapper.UnitTests) las compara contra el original. Los desajustes de carril de ciudad
/// (ids para otro país, nombre para Colombia) no se juzgan aquí, igual que en el pedido: sólo se rechaza un bloque sin ciudad o con una
/// pareja de ids a medias. Además, <c>Shipping</c> es nulo si y sólo si <c>ShippingSameAsBilling</c>.
/// </summary>
public sealed class BuyerProfileValidator : AbstractValidator<BuyerProfileDto>
{
    // Copiados de StoreRequestBilling, StoreRequestShipping, StorePlace y PublicOrderRequestBodyValidator.
    public const int NameMaxLength = 150;
    public const int IdentificationNumberMaxLength = 32;
    public const int EmailMaxLength = 254;
    public const int RecipientNameMaxLength = 120;
    public const int CityNameMaxLength = 120;
    public const int PhoneMaxLength = 32;
    public const int AddressMaxLength = 200;

    private const string Colombia = "CO";

    private static readonly string[] IdentificationTypes = ["NIT", "CC", "CE", "PASAPORTE"];

    public BuyerProfileValidator()
    {
        RuleFor(profile => profile.Billing).NotNull();
        RuleFor(profile => profile.Billing).SetValidator(new BillingValidator()).When(profile => profile.Billing is not null);

        RuleFor(profile => profile.Shipping)
            .Null()
            .WithMessage("Con 'misma dirección de envío que la de facturación' no se envía un bloque de envío.")
            .When(profile => profile.ShippingSameAsBilling);
        RuleFor(profile => profile.Shipping)
            .NotNull()
            .WithMessage("Sin 'misma dirección de envío que la de facturación' el bloque de envío es obligatorio.")
            .When(profile => !profile.ShippingSameAsBilling);
        RuleFor(profile => profile.Shipping!).SetValidator(new ShippingValidator()).When(profile => profile.Shipping is not null);
    }

    private static bool CountryShape(string? code) =>
        code is null || code.Trim() is { Length: 2 } trimmed && trimmed.All(char.IsAsciiLetter);

    private static bool HasCity(Guid? departmentId, Guid? cityId, string? cityName) =>
        departmentId.HasValue || cityId.HasValue || !string.IsNullOrWhiteSpace(cityName);

    // Con CO los dos ids van juntos y no vacíos; el resto (nombre para CO, ids para otro país) lo juzga quien resuelve el lugar.
    private static bool ColombianIdsComplete(string? country, Guid? departmentId, Guid? cityId) =>
        !string.Equals(country?.Trim(), Colombia, StringComparison.OrdinalIgnoreCase)
        || (departmentId.HasValue == cityId.HasValue && departmentId != Guid.Empty && cityId != Guid.Empty);

    private sealed class BillingValidator : AbstractValidator<BuyerBillingDto>
    {
        public BillingValidator()
        {
            RuleFor(b => b.Name).NotEmpty().MaximumLength(NameMaxLength);
            RuleFor(b => b.IdentificationType)
                .NotEmpty()
                .Must(type => type is null || IdentificationTypes.Contains(type.Trim(), StringComparer.OrdinalIgnoreCase))
                .WithMessage("El tipo de documento no es válido.");
            RuleFor(b => b.IdentificationNumber).NotEmpty().MaximumLength(IdentificationNumberMaxLength);
            RuleFor(b => b.Phone).NotEmpty().MaximumLength(PhoneMaxLength);
            RuleFor(b => b.Email).NotEmpty().MaximumLength(EmailMaxLength).EmailAddress();
            RuleFor(b => b.Address).NotEmpty().MaximumLength(AddressMaxLength);
            RuleFor(b => b.Country).NotEmpty().Must(CountryShape).WithMessage("El país debe ser un código de 2 letras.");
            RuleFor(b => b.CityName).MaximumLength(CityNameMaxLength);
            RuleFor(b => b.CityName)
                .Must((b, name) => HasCity(b.DepartmentId, b.CityId, name))
                .WithMessage("La ciudad es obligatoria.");
            RuleFor(b => b.CityId)
                .Must((b, id) => ColombianIdsComplete(b.Country, b.DepartmentId, id))
                .WithMessage("En Colombia el departamento y la ciudad van juntos.");
        }
    }

    private sealed class ShippingValidator : AbstractValidator<BuyerShippingDto>
    {
        public ShippingValidator()
        {
            RuleFor(s => s.RecipientName).MaximumLength(RecipientNameMaxLength);
            RuleFor(s => s.Phone).NotEmpty().MaximumLength(PhoneMaxLength);
            RuleFor(s => s.Address).NotEmpty().MaximumLength(AddressMaxLength);
            RuleFor(s => s.Country).NotEmpty().Must(CountryShape).WithMessage("El país debe ser un código de 2 letras.");
            RuleFor(s => s.CityName).MaximumLength(CityNameMaxLength);
            RuleFor(s => s.CityName)
                .Must((s, name) => HasCity(s.DepartmentId, s.CityId, name))
                .WithMessage("La ciudad es obligatoria.");
            RuleFor(s => s.CityId)
                .Must((s, id) => ColombianIdsComplete(s.Country, s.DepartmentId, id))
                .WithMessage("En Colombia el departamento y la ciudad van juntos.");
        }
    }
}
