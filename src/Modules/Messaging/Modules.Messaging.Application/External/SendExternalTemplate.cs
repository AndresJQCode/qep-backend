using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

/// <summary><c>POST /api/v1/external/whatsapp/messages</c>. Todo viaja como texto: un <c>clientId</c> mal formado tiene que ser un 422 con
/// su campo, no un cuerpo que no enlaza.</summary>
public sealed record SendExternalTemplateCommand(
    string? ClientId, string? ConnectionId, string? To, string? TemplateName, string? TemplateLanguage, IReadOnlyDictionary<string, string?>? Parameters)
    : ICommand<ExternalMessageResult>;

/// <summary><c>Replayed</c>: el mensaje ya existía con ese <c>clientId</c> y no se llamó a Meta (200 en vez de 201).</summary>
public sealed record ExternalMessageResult(Guid MessageId, Guid ConversationId, string Status, bool Replayed);

/// <summary>Spec 2026-10-30 §5.2. Las claves de <c>errors</c> son las del cuerpo JSON (<c>template.parameters.1</c>). Una plantilla sin
/// variables no necesita <c>parameters</c>.</summary>
public sealed class SendExternalTemplateValidator : AbstractValidator<SendExternalTemplateCommand>
{
    private const string TemplatePrefix = "template.";

    private static readonly Dictionary<string, string?> NoParameters = [];

    public SendExternalTemplateValidator(IDialablePhoneNumbers phones)
    {
        ArgumentNullException.ThrowIfNull(phones);
        RuleFor(command => command.ClientId).Must(IsUuid).WithMessage("Envía un UUID.").OverridePropertyName("clientId");
        RuleFor(command => command.ConnectionId).Must(IsUuid).WithMessage("Envía un UUID.").OverridePropertyName("connectionId");
        RuleFor(command => command.To)
            .Must(to => ExternalPhoneNumber.TryNormalize(to, out var waId) && phones.IsDialable(waId))
            .WithMessage("Envía el número con indicativo de país, por ejemplo 573001234567.")
            .OverridePropertyName("to");
        RuleFor(command => command.TemplateName)
            .NotEmpty().MaximumLength(SendTemplateMessageValidator.NameMaxLength).OverridePropertyName(TemplatePrefix + "name");
        RuleFor(command => command.TemplateLanguage)
            .NotEmpty().MaximumLength(SendTemplateMessageValidator.LanguageMaxLength).OverridePropertyName(TemplatePrefix + "language");
        RuleFor(command => command.Parameters).Custom((parameters, context) =>
        {
            foreach (var failure in TemplateParameterRules.Check(parameters ?? NoParameters))
            {
                context.AddFailure(new ValidationFailure(TemplatePrefix + failure.PropertyName, failure.ErrorMessage));
            }
        });
    }

    private static bool IsUuid(string? value) => Guid.TryParse(value, out var id) && id != Guid.Empty;
}

/// <summary>
/// Spec 2026-10-30 §6.3: <see cref="BillingTemplateOutbound"/> generalizado, para el sistema del tenant. No usa <see cref="OutboundGuards"/>
/// ni <c>TemplateOutbound.ResolveAsync</c>, que exigen una persona con sesión. Lo que manda el orden es la idempotencia: un reintento se
/// resuelve antes de mirar la plantilla, el destinatario o el estado de la conexión, porque lo que ya salió, ya salió, y un <c>clientId</c>
/// repetido nunca deja un hilo vacío. La carrera entre dos reintentos la cierra el reclamo: el segundo espera ahí y decide con lo que dejó el
/// primero, por las mismas reglas (<see cref="Repeat"/>).
/// </summary>
public sealed class SendExternalTemplateHandler(
    IApiCaller caller,
    ITenantModules tenantModules,
    IOutboundMessages outbound,
    IConversationQueries conversations,
    IConversationRepository repository,
    IConversationOpener opener,
    IMessagingConnectionDirectory connections,
    IMessageTemplates templates,
    IMessagingCustomerDirectory customers,
    IWhatsAppCloudClient meta,
    IMessagingNotifier notifier,
    ReadReceipts receipts,
    IClock clock,
    IValidator<SendExternalTemplateCommand> validator)
    : ICommandHandler<SendExternalTemplateCommand, ExternalMessageResult>
{
    private static readonly Dictionary<string, string?> NoParameters = [];

    public async Task<ExternalMessageResult> HandleAsync(SendExternalTemplateCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // 1. Scope, capacidad y número.
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var tenantId = caller.TenantId;
        var clientId = Guid.Parse(command.ClientId!);
        var connectionId = Guid.Parse(command.ConnectionId!);
        ExternalPhoneNumber.TryNormalize(command.To, out var waId);
        await ExternalApiAccess.EnsureAsync(caller, tenantModules, MessagingScopes.TemplatesSend, cancellationToken);
        await ExternalApiAccess.EnsureConnectionAsync(caller, connections, connectionId, cancellationToken);

        // 2. Idempotencia antes de todo, por tenant y no por clave: rotar una clave en medio de un reintento no duplica.
        if (await outbound.FindApiMessageAsync(tenantId, clientId, cancellationToken) is { } existing)
        {
            if (existing.ConnectionId != connectionId || !string.Equals(existing.WaId, waId, StringComparison.Ordinal))
            {
                throw ClientIdReused();
            }

            if (!CanResend(existing.Status, existing.FailureCode))
            {
                return Repeat(existing.Id, existing.ConversationId, existing.Status, existing.DetailsJson, command);
            }
        }

        // 3. Antes de abrir la conversación: una plantilla mala no deja un hilo vacío.
        var resolution = await connections.ResolveSenderWithReasonAsync(tenantId, connectionId, cancellationToken);
        if (resolution.Sender is not { } sender)
        {
            throw new MessagingDomainException(MessagingErrorCodes.ConnectionUnavailable, "The WhatsApp connection is paused, needs attention or was deleted.");
        }

        // El validador ya garantizó que ningún valor es nulo.
        var values = (command.Parameters ?? NoParameters).ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal);
        var catalog = await templates.ListAsync(tenantId, connectionId, refresh: false, cancellationToken);
        var template = TemplateCatalog.RequireSendable(catalog, command.TemplateName!, command.TemplateLanguage!, values.Keys);
        var built = TemplateCatalog.BuildSend(template, values);

        // 4. Una sola coincidencia liga; con varias o ninguna no hay a quién, y esta API no crea clientes.
        var matches = await customers.ListByPhoneAsync(tenantId, waId, take: 2, cancellationToken);
        Guid? customerId = matches.Count == 1 ? await customers.ResolveSurvivorAsync(tenantId, matches[0].Id, cancellationToken) : null;

        // 5. Las reglas también leen el cliente ya ligado a la conversación: su teléfono puede no coincidir con este número.
        var found = await opener.FindByPhoneAsync(tenantId, connectionId, waId, cancellationToken) is { } foundId
            ? await conversations.FindAsync(tenantId, foundId, cancellationToken)
            : null;
        var contacts = await LoadContactsAsync(tenantId, matches, found?.CustomerId, cancellationToken);
        if (ExternalRecipientPolicy.Evaluate(template.Category, found?.BlockedAt is not null, contacts) is { } refusal)
        {
            throw new MessagingDomainException(refusal, "El destinatario no puede recibir esta plantilla.");
        }

        // 6. Una conversación existente no cambia de estado ni de asignación; una nueva nace Open y sin asignar.
        var now = clock.UtcNow;
        var opened = await opener.OpenByPhoneAsync(
            tenantId, connectionId, waId, customerId, matches.Count == 1 ? matches[0].Name : string.Empty, now, cancellationToken);
        var conversation = await conversations.FindAsync(tenantId, opened.ConversationId, cancellationToken)
            ?? throw new InvalidOperationException("The conversation that was just opened could not be read.");

        // 7. Un request en carrera con el mismo clientId espera acá hasta que el primero cierre, y lee lo que este dejó.
        var draft = new OutboundDraft(
            Guid.CreateVersion7(), conversation.Id, tenantId, connectionId, clientId, built.Text, SentByMemberId: null, now,
            Kind: MessageKind.Template, DetailsJson: built.DetailsJson, SentByApiKey: new OutboundApiKeyAuthor(caller.ApiKeyId, caller.ApiKeyName));
        await using var claim = await outbound.ClaimAsync(draft, cancellationToken);
        if (claim.Existing is { } held)
        {
            // Sin clave de autor es el mensaje de otra persona en este hilo: nunca se devuelve como si fuera de este sistema.
            if (held.SentByApiKeyId is null)
            {
                await claim.RollbackAsync(CancellationToken.None);
                throw ClientIdReused();
            }

            if (!CanResend(held.Status, held.FailureCode))
            {
                await claim.RollbackAsync(CancellationToken.None);
                return Repeat(held.Id, conversation.Id, held.Status, held.DetailsJson, command);
            }
        }

        // 8. Desde acá todo va con CancellationToken.None: si Meta aceptó, cancelar no puede borrar la fila.
        var result = await meta.SendTemplateAsync(
            sender, SendTarget.ForTemplate(conversation.UserId, conversation.WaId), built.Send,
            SendMessageHandler.CallbackPrefix + claim.MessageId.ToString("D"), CancellationToken.None);

        // Meta avisa que la persona apagó el marketing; se anota antes de cerrar el reclamo, que lanza.
        if (result is { Outcome: SendOutcome.GraphError, Code: MetaErrorCodes.MarketingOptOut } && (conversation.CustomerId ?? customerId) is { } optedOut)
        {
            await customers.MarkMarketingOptOutAsync(tenantId, optedOut, CancellationToken.None);
        }

        try
        {
            await new OutboundSettlement(repository, connections, notifier, receipts, clock)
                .SettleAsync(tenantId, connectionId, claim, result, clock.UtcNow, metaMediaId: null, afterRollback: null, seen: 0);
        }
        catch (MessagingDomainException exception)
        {
            throw ExternalSendFailure.Map(result, exception);
        }

        return new ExternalMessageResult(claim.MessageId, conversation.Id, nameof(MessageStatus.Sent), Replayed: false);
    }

    private async Task<IReadOnlyList<MessagingCustomerContact>> LoadContactsAsync(
        Guid tenantId, IReadOnlyList<CustomerRefDto> matches, Guid? linkedCustomerId, CancellationToken cancellationToken)
    {
        var ids = matches.Select(match => match.Id).ToHashSet();
        if (linkedCustomerId is { } linked)
        {
            ids.Add(linked);
        }

        var contacts = new List<MessagingCustomerContact>(ids.Count);
        foreach (var id in ids)
        {
            if (await customers.FindContactAsync(tenantId, id, cancellationToken) is { } contact)
            {
                contacts.Add(contact);
            }
        }

        return contacts;
    }

    /// <summary>Spec 2026-10-30 §6.3.2: sólo un <c>Failed</c> cierto se reenvía. <c>Unconfirmed</c> significa que Meta pudo haberlo aceptado, y
    /// reenviar ahí mandaría dos mensajes.</summary>
    private static bool CanResend(MessageStatus status, int? failureCode) =>
        status == MessageStatus.Failed && failureCode != MessageFailureReasons.Unconfirmed;

    /// <summary>Lo que responde un <c>clientId</c> que ya no se reenvía: otra plantilla es 409; la misma, lo guardado, o el 502 si nunca se
    /// supo si salió.</summary>
    private static ExternalMessageResult Repeat(Guid messageId, Guid conversationId, MessageStatus status, string? detailsJson, SendExternalTemplateCommand command)
    {
        if (!SameTemplate(detailsJson, command))
        {
            throw ClientIdReused();
        }

        return status == MessageStatus.Failed
            ? throw ExternalSendFailure.Unconfirmed()
            : new ExternalMessageResult(messageId, conversationId, status.ToString(), Replayed: true);
    }

    private static bool SameTemplate(string? detailsJson, SendExternalTemplateCommand command)
    {
        if (TemplateDetailsJson.ParseSend(detailsJson) is not { } stored
            || !string.Equals(stored.Name, command.TemplateName, StringComparison.Ordinal)
            || !string.Equals(stored.Language, command.TemplateLanguage, StringComparison.Ordinal))
        {
            return false;
        }

        var parameters = command.Parameters ?? NoParameters;
        return stored.Parameters.Count == parameters.Count
            && parameters.All(pair => stored.Parameters.TryGetValue(pair.Key, out var value) && string.Equals(value, pair.Value, StringComparison.Ordinal));
    }

    private static ResourceConflictException ClientIdReused() =>
        new(MessagingErrorCodes.ExternalClientIdReused, "Ese clientId ya se usó con otro destino o plantilla.");
}
