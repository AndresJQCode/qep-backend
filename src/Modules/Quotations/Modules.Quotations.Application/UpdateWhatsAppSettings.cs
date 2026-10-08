using System.Text.RegularExpressions;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// El PUT de la configuración de WhatsApp (spec 2026-10-07). <c>Mode</c> como texto: es el
/// validador el que dice si es uno de los tres. Los campos de la cuenta propia son opcionales:
/// nulos conservan lo guardado, y fuera de <c>Own</c> se ignoran.
///
/// <see cref="ToString"/> esconde la key: el de un record imprime todas sus propiedades, y basta
/// un <c>{Command}</c> en un log o un depurador que lo imprima para filtrarla.
/// </summary>
public sealed record UpdateWhatsAppSettingsCommand(
    Guid TenantId,
    string Mode,
    string? Provider,
    string? ApiKey,
    string? FromNumber,
    string? TemplateId,
    long ExpectedVersion) : ICommand<WhatsAppSettingsDto>
{
    public override string ToString() =>
        $"UpdateWhatsAppSettingsCommand {{ TenantId = {TenantId}, Mode = {Mode}, Provider = {Provider}, "
        + $"ApiKey = {(ApiKey is null ? "null" : "***")}, FromNumber = {FromNumber}, "
        + $"TemplateId = {TemplateId}, ExpectedVersion = {ExpectedVersion} }}";
}

/// <summary>
/// Sólo <b>formato</b> y sólo de lo que viene (spec 2026-10-07, «Validador»): el validador no ve la
/// fila, así que lo requerido en Own lo decide el handler. Los mensajes de los cuatro campos que la
/// pantalla marca van en español; los de <c>Mode</c> y <c>ExpectedVersion</c>, inalcanzables desde
/// la pantalla, en inglés como el resto del módulo. Ninguno usa <c>{PropertyValue}</c>: el mensaje
/// termina en <c>ValidationException.Message</c>, que llega a ProblemDetails, al log y a
/// <c>platform.request_failures</c>.
/// </summary>
public sealed partial class UpdateWhatsAppSettingsValidator : AbstractValidator<UpdateWhatsAppSettingsCommand>
{
    public const string ProviderInvalidMessage = "Elige un proveedor válido: hoy sólo Zenvia.";

    public const string ApiKeyInvalidMessage =
        "La API key sólo puede tener letras, números y símbolos, sin espacios: vuelve a copiarla de Zenvia.";

    public const string FromNumberInvalidMessage =
        "Escribe el número con indicativo de país, sólo dígitos y sin '+' (entre 10 y 15).";

    public const string TemplateIdInvalidMessage =
        "El id de la plantilla tiene la forma 9b2f4c1e-3d5a-4e6b-8c7d-1a2b3c4d5e6f.";

    [GeneratedRegex(@"^[\x21-\x7E]{1,512}$", RegexOptions.CultureInvariant)]
    private static partial Regex ApiKeyPattern();

    [GeneratedRegex("^[0-9]{10,15}$", RegexOptions.CultureInvariant)]
    private static partial Regex FromNumberPattern();

    public UpdateWhatsAppSettingsValidator()
    {
        RuleFor(command => command.Mode)
            .Must(mode => WhatsAppModes.TryParse(mode, out _))
            .WithMessage("'Mode' must be 'Shared', 'Own' or 'Disabled'.");
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);

        When(command => string.Equals(command.Mode, nameof(WhatsAppMode.Own), StringComparison.Ordinal), () =>
        {
            RuleFor(command => command.Provider)
                .Must(WhatsAppModes.IsProvider)
                .When(command => command.Provider is not null)
                .WithMessage(ProviderInvalidMessage);
            RuleFor(command => command.ApiKey)
                .Must(apiKey => ApiKeyPattern().IsMatch(apiKey!.Trim()))
                .When(command => command.ApiKey is not null)
                .WithMessage(ApiKeyInvalidMessage);
            RuleFor(command => command.FromNumber)
                .Must(fromNumber => FromNumberPattern().IsMatch(fromNumber!.Trim()))
                .When(command => command.FromNumber is not null)
                .WithMessage(FromNumberInvalidMessage);
            RuleFor(command => command.TemplateId)
                .Must(templateId => Guid.TryParseExact(templateId!.Trim(), "D", out _))
                .When(command => command.TemplateId is not null)
                .WithMessage(TemplateIdInvalidMessage);
        });
    }
}
