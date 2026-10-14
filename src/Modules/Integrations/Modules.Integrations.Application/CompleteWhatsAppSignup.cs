using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Integrations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Integrations.Application;

/// <summary>El cuerpo de <c>POST /integrations/whatsapp/embedded-signup</c> (spec 2026-10-09 §5.2).
/// <see cref="ToString"/> no imprime el <c>code</c> ni el <c>Pin</c>.</summary>
/// <param name="Pin">El PIN de verificación en dos pasos que ya tiene el número, si lo tiene: Meta exige el
/// vigente en <c>/register</c>. <c>null</c> deja que el handler genere uno. Se ignora en coexistencia.</param>
public sealed record CompleteWhatsAppSignupCommand(
    Guid TenantId,
    string? Name,
    string? Path,
    string? Event,
    string? Code,
    string? WabaId,
    string? PhoneNumberId,
    string? BusinessId,
    string? Pin = null) : ICommand<ConnectionResponse>
{
    public const string EventFinish = "FINISH";
    public const string EventCoexistence = "FINISH_WHATSAPP_BUSINESS_APP_ONBOARDING";
    public const string PathExisting = "existing_business_app";
    public const string PathNewNumber = "new_number";

    public bool IsCoexistence => string.Equals(Event, EventCoexistence, StringComparison.Ordinal);

    public override string ToString() =>
        $"CompleteWhatsAppSignupCommand {{ TenantId = {TenantId}, Name = {Name}, Path = {Path}, Event = {Event}, "
        + $"WabaId = {WabaId}, PhoneNumberId = {PhoneNumberId}, BusinessId = {BusinessId} }}";
}

/// <summary>Spec §8.1, paso 2. Los dígitos se exigen con regex, nunca con el patrón del catálogo: el
/// código de error viaja por campo. Cada regla nombra su clave en camelCase, que es la que lee el
/// formulario en <c>errors</c>.</summary>
public sealed partial class CompleteWhatsAppSignupValidator : AbstractValidator<CompleteWhatsAppSignupCommand>
{
    public const string DigitsMessage = "Meta devolvió un identificador que no es numérico; vuelve a abrir el flujo.";

    public const string PinMessage = "Escribe los seis dígitos del PIN, o déjalo vacío si el número no tiene verificación en dos pasos.";

    [GeneratedRegex(@"^[0-9]{1,32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Digits();

    [GeneratedRegex(@"^[0-9]{6}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SixDigits();

    public CompleteWhatsAppSignupValidator()
    {
        RuleFor(command => command.Name)
            .Custom((name, context) => ConnectionInputRules.AddTo(context, ConnectionInputRules.CheckName(name)));
        RuleFor(command => command.Path)
            .Must(path => path is CompleteWhatsAppSignupCommand.PathExisting or CompleteWhatsAppSignupCommand.PathNewNumber)
            .OverridePropertyName("path")
            .WithMessage("Elige cómo conectar el número.");
        RuleFor(command => command.Event)
            .Must(value => value is CompleteWhatsAppSignupCommand.EventFinish or CompleteWhatsAppSignupCommand.EventCoexistence)
            .OverridePropertyName("event")
            .WithMessage("Meta no terminó el flujo; vuelve a intentarlo.");
        RuleFor(command => command.Code)
            .Must(code => !string.IsNullOrWhiteSpace(code) && code.Length <= 1024 && !code.Any(char.IsControl))
            .OverridePropertyName("code")
            .WithMessage("Meta no devolvió un código válido; vuelve a abrir el flujo.");
        RuleFor(command => command.WabaId)
            .Must(value => value is not null && Digits().IsMatch(value))
            .OverridePropertyName("wabaId")
            .WithMessage(DigitsMessage);
        RuleFor(command => command.PhoneNumberId)
            .Must((command, value) => command.IsCoexistence ? value is null : value is not null && Digits().IsMatch(value))
            .OverridePropertyName("phoneNumberId")
            .WithMessage(DigitsMessage);
        RuleFor(command => command.BusinessId)
            .Must(value => value is null || Digits().IsMatch(value))
            .OverridePropertyName("businessId")
            .WithMessage(DigitsMessage);
        // `""` no es «sin PIN»: el formulario manda null cuando el campo queda vacío.
        RuleFor(command => command.Pin)
            .Must(value => value is null || SixDigits().IsMatch(value))
            .OverridePropertyName("pin")
            .WithMessage(PinMessage);
    }
}

/// <summary>
/// Spec 2026-10-09 §8.1. El <c>code</c> vence a los 30 s: antes del canje sólo hay validaciones de
/// milisegundos. Después del canje nada se deshace en Meta: volver a intentar exige otro <c>code</c> y
/// los pasos son seguros de repetir. La conexión, la ruta y la auditoría commitean juntas; una carrera
/// con otro tenant por el mismo número la resuelve <c>IX_connection_routes_provider_external</c>.
/// </summary>
public sealed class CompleteWhatsAppSignupHandler(
    IIntegrationProviderCatalog catalog,
    IIntegrationConnectionRepository repository,
    IConnectionRouteRepository routes,
    IIntegrationsUnitOfWork unitOfWork,
    IIntegrationsAuditRecorder auditRecorder,
    ISecretProtector protector,
    IWhatsAppSignupGateway gateway,
    IMetaAppSettings metaApp,
    ITenantModules tenantModules,
    IMembershipDirectory membershipDirectory,
    IConnectionAuthorNames authorNames,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<CompleteWhatsAppSignupCommand> validator)
    : ICommandHandler<CompleteWhatsAppSignupCommand, ConnectionResponse>
{
    public const string AmbiguousNumbersDetail =
        "La cuenta tiene varios números sin conectar; deja uno solo o conéctalo desde el número nuevo.";

    public const string PinMismatchMessage = "El PIN no coincide con el que tiene el número. Revísalo en WhatsApp Manager.";

    /// <summary>Graph: «Two step verification PIN mismatch».</summary>
    private const int GraphPinMismatch = 133005;

    /// <summary>Graph: «Registration/Deregistration failed because of too many attempts» (10 en 72 h).</summary>
    private const int GraphRegistrationLimit = 133016;

    public async Task<ConnectionResponse> HandleAsync(CompleteWhatsAppSignupCommand command, CancellationToken cancellationToken)
    {
        IntegrationsAuthorization.EnsureAuthorized(executionContext, command.TenantId, IntegrationsPermissions.ConnectionManage);
        SecretProtectionGuard.EnsureAvailable(protector);
        var provider = catalog.Find(IntegrationProviders.WhatsAppCloud.Key)
            ?? throw new InvalidOperationException("The whatsapp-cloud provider is not in the catalog.");
        await ProviderVisibility.EnsureVisibleAsync(tenantModules, command.TenantId, provider, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var author = await IntegrationsMember.ResolveAsync(membershipDirectory, executionContext, command.TenantId, cancellationToken);

        // Paso 3: una lectura indexada; la garantía final la dan los índices únicos al guardar.
        if (await repository.CountAsync(command.TenantId, provider.Key, cancellationToken) >= provider.MaxConnections)
        {
            throw new IntegrationsDomainException(
                IntegrationsErrorCodes.LimitReached, $"A tenant can have at most {provider.MaxConnections} connections of this provider.");
        }

        if ((await repository.ListAsync(command.TenantId, cancellationToken)).Any(connection =>
                string.Equals(connection.ProviderKey, provider.Key, StringComparison.Ordinal)
                && string.Equals(connection.Name, command.Name!.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            throw new IntegrationsDomainException(IntegrationsErrorCodes.NameTaken, "Another connection of this provider already uses that name.");
        }

        if (command.PhoneNumberId is { } requested && await routes.ExistsAsync(provider.Key, requested, cancellationToken))
        {
            throw NumberAlreadyConnected();
        }

        // D-M3: sin la app no hay con qué canjear; el log dice la causa real.
        if (!metaApp.IsConfigured)
        {
            throw new IntegrationsDomainException(
                IntegrationsErrorCodes.WhatsAppCodeExchangeFailed, "Meta:App is not configured; the code cannot be exchanged.");
        }

        // Paso 4.
        var exchange = await gateway.ExchangeCodeAsync(command.Code!, cancellationToken);
        if (!exchange.Succeeded || exchange.Value is not { } token || string.IsNullOrWhiteSpace(token))
        {
            throw new IntegrationsDomainException(IntegrationsErrorCodes.WhatsAppCodeExchangeFailed, "Meta did not exchange the code.");
        }

        string phoneNumberId;
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal) { [WhatsAppCloudFieldKeys.AccessToken] = token };
        if (command.IsCoexistence)
        {
            // Paso 6: el número ya está registrado en la app del teléfono; no se llama /register.
            phoneNumberId = await ResolveCoexistenceNumberAsync(provider, command.WabaId!, token, cancellationToken);
        }
        else
        {
            // Paso 5 (§8.1). El PIN es el del negocio si el número ya tiene verificación en dos pasos; si no,
            // uno aleatorio que la crea. Se guarda cifrado porque Meta exige el vigente en cada /register.
            phoneNumberId = command.PhoneNumberId!;
            var pin = command.Pin ?? RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
            var registered = await gateway.RegisterNumberAsync(phoneNumberId, token, pin, cancellationToken);
            if (!registered.Succeeded)
            {
                throw RegisterFailed(registered.Failure);
            }

            secrets[WhatsAppCloudFieldKeys.RegistrationPin] = pin;
        }

        // Paso 7 y 8.
        EnsureSucceeded(await gateway.SubscribeAppAsync(command.WabaId!, token, cancellationToken), "subscribe");
        var read = await gateway.GetPhoneNumberAsync(phoneNumberId, token, cancellationToken);
        if (!read.Succeeded || read.Value is null)
        {
            throw RegistrationFailed("read");
        }

        // Paso 9: todo en una transacción.
        var now = clock.UtcNow;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [WhatsAppCloudFieldKeys.PhoneNumberId] = phoneNumberId,
            [WhatsAppCloudFieldKeys.WabaId] = command.WabaId!,
        };
        Put(fields, WhatsAppCloudFieldKeys.DisplayPhoneNumber, read.Value.DisplayPhoneNumber);
        Put(fields, WhatsAppCloudFieldKeys.VerifiedName, read.Value.VerifiedName);
        Put(fields, WhatsAppCloudFieldKeys.QualityRating, read.Value.QualityRating);
        var connection = IntegrationConnection.Create(
            provider, command.TenantId, command.Name!, fields, secrets, protector.Protect, author, now);
        repository.Add(connection);
        routes.Add(IntegrationConnectionRoute.Create(provider.Key, phoneNumberId, command.WabaId, command.TenantId, connection.Id));
        // changedFields por clave, más path y event como metadatos (§6.1); nunca un valor.
        string[] audited = [.. ConnectionAudit.KeysOf(connection), $"path:{command.Path}", $"event:{command.Event}"];
        ConnectionAudit.ByMember(auditRecorder, executionContext, connection, ConnectionAuditActions.Created, audited, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await ConnectionMapping.ToResponseAsync(connection, provider, protector, authorNames, cancellationToken);
    }

    private async Task<string> ResolveCoexistenceNumberAsync(IntegrationProvider provider, string wabaId, string token, CancellationToken cancellationToken)
    {
        var numbers = await gateway.ListPhoneNumbersAsync(wabaId, token, cancellationToken);
        if (!numbers.Succeeded || numbers.Value is null || numbers.Value.Count == 0)
        {
            throw RegistrationFailed("numbers");
        }

        if (numbers.Value.Count == 1)
        {
            return numbers.Value[0].Id;
        }

        var free = new List<string>();
        foreach (var number in numbers.Value)
        {
            if (!await routes.ExistsAsync(provider.Key, number.Id, cancellationToken))
            {
                free.Add(number.Id);
            }
        }

        return free.Count switch
        {
            1 => free[0],
            0 => throw NumberAlreadyConnected(),
            // D-M15: no se adivina.
            _ => throw new IntegrationsDomainException(IntegrationsErrorCodes.WhatsAppRegistrationFailed, AmbiguousNumbersDetail),
        };
    }

    private static void EnsureSucceeded<T>(GraphResult<T> result, string step)
    {
        if (!result.Succeeded)
        {
            throw RegistrationFailed(step);
        }
    }

    private static IntegrationsDomainException RegistrationFailed(string step) =>
        new(IntegrationsErrorCodes.WhatsAppRegistrationFailed, $"Meta rejected the '{step}' step of the WhatsApp registration.");

    /// <summary>Los dos códigos de Graph que la persona puede resolver reciben su propia respuesta: el PIN
    /// como error del campo, que el formulario marca; el límite con código propio, para que no invite a
    /// reintentar. El resto es <see cref="RegistrationFailed"/> como cualquier paso.</summary>
    private static IntegrationsDomainException RegisterFailed(GraphFailure? failure) =>
        failure?.Code switch
        {
            GraphPinMismatch => new IntegrationsDomainException(
                IntegrationsErrorCodes.WhatsAppRegistrationFailed,
                "Meta rejected the 'register' step: the two-step verification PIN does not match.",
                new Dictionary<string, string[]>(StringComparer.Ordinal) { ["pin"] = [PinMismatchMessage] }),
            GraphRegistrationLimit => new IntegrationsDomainException(
                IntegrationsErrorCodes.WhatsAppRegistrationLimit,
                "Meta rejected the 'register' step: too many registrations of this number in 72 hours."),
            _ => RegistrationFailed("register"),
        };

    private static IntegrationsDomainException NumberAlreadyConnected() =>
        new(IntegrationsErrorCodes.WhatsAppNumberAlreadyConnected, "That WhatsApp number is already connected.");

    private static void Put(Dictionary<string, string> fields, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            fields[key] = value.Trim();
        }
    }
}
