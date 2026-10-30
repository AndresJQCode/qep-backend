using FluentValidation;
using Modules.ServiceContracts.Domain;

namespace Modules.ServiceContracts.Application;

/// <summary>
/// El cuerpo del perfil de cobro tal como llega por el cable (<c>{ channel, assigneeMemberId }</c>), con los dos campos
/// anulables para que su ausencia sea un <c>validation.failed</c> con el campo y no un 400 del binding. Lo reutiliza la
/// creación de servicios, que acepta el mismo perfil.
/// </summary>
public sealed record ProfileInput(string? Channel, Guid? AssigneeMemberId);

public sealed class ProfileInputValidator : AbstractValidator<ProfileInput>
{
    public ProfileInputValidator()
    {
        RuleFor(input => input.Channel).MustBeABillingChannel();
        RuleFor(input => input.AssigneeMemberId).NotNull().NotEqual(Guid.Empty);
    }
}

/// <summary>Las reglas del perfil de cobro (spec cobros-recurrentes §3.1), compartidas por el PUT del perfil y la creación de servicios.</summary>
internal static class ProfileRules
{
    /// <summary>
    /// Sólo los dos nombres exactos del contrato: <c>Enum.TryParse</c> aceptaría «1», «whatsapp» o un número fuera del
    /// enum, que el cable nunca debe producir.
    /// </summary>
    public static bool TryParseChannel(string? text, out BillingChannel channel)
    {
        switch (text)
        {
            case nameof(BillingChannel.WhatsApp):
                channel = BillingChannel.WhatsApp;
                return true;
            case nameof(BillingChannel.Email):
                channel = BillingChannel.Email;
                return true;
            default:
                channel = default;
                return false;
        }
    }

    /// <summary>
    /// WhatsApp exige teléfono y Email exige correo en el cliente. Un valor en blanco cuenta como faltante. Sólo se valida al
    /// guardar: si el cliente pierde el dato después, el aviso falla con <c>no_phone</c> / <c>no_email</c> (§3.4) y no se
    /// cambia el canal.
    /// </summary>
    public static void EnsureChannelIsReachable(BillingChannel channel, ServiceContractsCustomerRef customer)
    {
        switch (channel)
        {
            case BillingChannel.WhatsApp when string.IsNullOrWhiteSpace(customer.PhoneE164):
                throw new ServiceContractsDomainException(
                    ServiceContractsErrorCodes.ProfileWhatsAppRequiresPhone,
                    "WhatsApp needs the customer to have a phone number.");
            case BillingChannel.Email when string.IsNullOrWhiteSpace(customer.Email):
                throw new ServiceContractsDomainException(
                    ServiceContractsErrorCodes.ProfileEmailRequiresAddress,
                    "Email needs the customer to have an email address.");
        }
    }

    /// <summary>
    /// El encargado es una membresía activa cuyos roles conceden <c>billing.service.manage</c> (owner, 2026-10-29: tiene que
    /// poder actuar sobre lo que el email diario le pide). <c>null</c> es una membresía que no existe en el tenant.
    /// </summary>
    public static void EnsureAssigneeCanManage(ServiceContractsMemberRef? member)
    {
        if (member is not { IsActive: true, CanManage: true })
        {
            throw new ServiceContractsDomainException(
                ServiceContractsErrorCodes.ProfileAssigneeInvalid,
                "The assignee must be an active member who can manage billing.");
        }
    }

    public static IRuleBuilderOptions<T, string?> MustBeABillingChannel<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(text => TryParseChannel(text, out _)).WithMessage("Channel must be WhatsApp or Email.");
}
