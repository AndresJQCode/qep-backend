using System.Text.RegularExpressions;
using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

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

/// <summary>
/// Guarda la configuración de WhatsApp (spec 2026-10-07, «Casos de uso»). Mismo esqueleto que
/// <see cref="UpdateOrdersExportLayoutHandler"/>, en este orden:
/// <list type="number">
/// <item>autoriza antes de validar (hallazgo B1 del spec del layout: un 422 a quien no tiene permiso
/// confirma que el cuerpo se leyó);</item>
/// <item>gate de capacidad: <c>tenancy.*</c> es núcleo y el enmascaramiento no apaga este endpoint
/// aunque el tenant no tenga cotizaciones;</item>
/// <item>formato (validador);</item>
/// <item>fila o <see cref="TenantWhatsAppSettings.CreateEmpty"/>, y versión (412);</item>
/// <item>en Own, lo requerido según la fila, todo junto en una sola ValidationException;</item>
/// <item>fuera de Own, los campos propios del cuerpo se ignoran;</item>
/// <item>drenaje de rotación, que se salta si la key no descifra (decisión 25);</item>
/// <item>un solo Configure: una versión por guardado;</item>
/// <item>si cambió algo: Add si no había fila, una auditoría por clase de cambio, un guardado.</item>
/// </list>
/// </summary>
public sealed class UpdateWhatsAppSettingsHandler(
    ITenantWhatsAppSettingsRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IWhatsAppSecretProtector protector,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<UpdateWhatsAppSettingsCommand> validator)
    : ICommandHandler<UpdateWhatsAppSettingsCommand, WhatsAppSettingsDto>
{
    public const string ModeChangedAction = "quotations.whatsapp_settings.mode_changed";
    public const string ApiKeyReplacedAction = "quotations.whatsapp_settings.api_key_replaced";
    public const string UpdatedAction = "quotations.whatsapp_settings.updated";

    public const string ProviderRequiredMessage = "Elige el proveedor de tu cuenta de WhatsApp.";
    public const string ApiKeyRequiredMessage = "Pega la API key de tu cuenta de Zenvia.";
    public const string ApiKeyUnreadableMessage = "La API key guardada ya no se puede leer: vuelve a pegarla.";
    public const string FromNumberRequiredMessage = "Escribe el número emisor de tu cuenta de Zenvia.";
    public const string TemplateIdRequiredMessage = "Escribe el id de la plantilla aprobada.";

    public async Task<WhatsAppSettingsDto> HandleAsync(
        UpdateWhatsAppSettingsCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, TenancyPermissions.SettingsUpdate);
        await TenantModuleGuard.EnsureEnabledAsync(
            tenantModules, command.TenantId, TenantModuleKeys.Quotations, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var now = clock.UtcNow;
        var stored = await repository.FindAsync(command.TenantId, cancellationToken);
        var settings = stored ?? TenantWhatsAppSettings.CreateEmpty(command.TenantId, now);
        if (settings.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The WhatsApp settings changed after they were loaded.");
        }

        if (!WhatsAppModes.TryParse(command.Mode, out var mode))
        {
            // El validador ya lo rechazó; llegar acá es un error de programación.
            throw new InvalidOperationException("The WhatsApp mode was not validated.");
        }

        // Decisión 28: fuera de Own los campos de la cuenta propia ni se validan ni se guardan.
        var own = mode == WhatsAppMode.Own;
        WhatsAppProvider? provider = own && command.Provider is not null ? WhatsAppProvider.Zenvia : null;
        var apiKey = own ? command.ApiKey?.Trim() : null;
        var fromNumber = own ? command.FromNumber?.Trim() : null;
        var templateId = own ? command.TemplateId?.Trim() : null;

        if (own)
        {
            EnsureOwnIsComplete(settings, provider, apiKey, fromNumber, templateId);
        }

        var rekeyed = apiKey is null ? Drain(settings) : null;
        var newToken = apiKey is null ? null : protector.Protect(command.TenantId, apiKey);
        var changes = settings.Configure(mode, provider, newToken, rekeyed, fromNumber, templateId, now);
        if (!changes.Any)
        {
            return WhatsAppSettingsMappings.ToDto(settings, protector);
        }

        if (stored is null)
        {
            repository.Add(settings);
        }

        Audit(command.TenantId, changes, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return WhatsAppSettingsMappings.ToDto(settings, protector);
    }

    // Lo requerido en Own depende de la fila: por eso no es del validador (decisión 29). Un error
    // por campo, todos juntos, en el mismo mapa errors que el validador.
    private void EnsureOwnIsComplete(
        TenantWhatsAppSettings settings,
        WhatsAppProvider? provider,
        string? apiKey,
        string? fromNumber,
        string? templateId)
    {
        var failures = new List<ValidationFailure>();
        if (provider is null && settings.Provider is null)
        {
            failures.Add(new ValidationFailure(nameof(UpdateWhatsAppSettingsCommand.Provider), ProviderRequiredMessage));
        }

        if (apiKey is null)
        {
            if (settings.ApiToken is null)
            {
                failures.Add(new ValidationFailure(nameof(UpdateWhatsAppSettingsCommand.ApiKey), ApiKeyRequiredMessage));
            }
            else if (!protector.TryUnprotect(settings.TenantId, settings.ApiToken, out _))
            {
                failures.Add(new ValidationFailure(nameof(UpdateWhatsAppSettingsCommand.ApiKey), ApiKeyUnreadableMessage));
            }
        }

        if (fromNumber is null && settings.FromNumber is null)
        {
            failures.Add(new ValidationFailure(nameof(UpdateWhatsAppSettingsCommand.FromNumber), FromNumberRequiredMessage));
        }

        if (templateId is null && settings.TemplateId is null)
        {
            failures.Add(new ValidationFailure(nameof(UpdateWhatsAppSettingsCommand.TemplateId), TemplateIdRequiredMessage));
        }

        if (failures.Count > 0)
        {
            throw new ValidationException(failures);
        }
    }

    // Red adicional de la rotación (spec, «Rotación», punto 4): la key guardada en una llave que no
    // es la activa se re-cifra en cualquier PUT que no traiga apiKey. Si no descifra, se salta.
    private ProtectedSecret? Drain(TenantWhatsAppSettings settings)
    {
        var active = protector.ActiveKeyId;
        if (settings.ApiToken is not { } current || active is null ||
            string.Equals(current.KeyId, active, StringComparison.Ordinal))
        {
            return null;
        }

        return protector.TryUnprotect(settings.TenantId, current, out var plaintext)
            ? protector.Protect(settings.TenantId, plaintext!)
            : null;
    }

    // El publicador no lleva campos: la acción dice la clase de cambio. Ni el modo de destino, ni
    // el número, ni la plantilla, ni la key.
    private void Audit(Guid tenantId, WhatsAppSettingsChanges changes, DateTimeOffset now)
    {
        if (changes.ModeChanged)
        {
            Publish(tenantId, ModeChangedAction, now);
        }

        if (changes.ApiKeyReplaced)
        {
            Publish(tenantId, ApiKeyReplacedAction, now);
        }

        if (changes.DetailsChanged || changes.KeyRotated)
        {
            Publish(tenantId, UpdatedAction, now);
        }
    }

    private void Publish(Guid tenantId, string action, DateTimeOffset now) =>
        auditPublisher.Publish(
            tenantId, executionContext.SubjectId, action, tenantId.ToString(), "success", now);
}
