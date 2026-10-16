using BuildingBlocks.Application;
using FluentValidation;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-11 §5.2: <c>{ conversation, message }</c>.</summary>
public sealed record StartedConversationDto(ConversationSummary Conversation, MessageDto Message);

/// <summary><c>Created</c> decide el status: 201 si la conversación se creó, 200 si ya existía y se envió ahí.</summary>
public sealed record StartConversationResult(StartedConversationDto Body, bool Created);

public sealed record StartConversationCommand(
    Guid TenantId,
    Guid? ClientId,
    Guid? ConnectionId,
    Guid? CustomerId,
    string? TemplateName,
    string? TemplateLanguage,
    IReadOnlyDictionary<string, string?>? Parameters) : ICommand<StartConversationResult>;

/// <summary>Las claves de <c>errors</c> son las del cuerpo JSON: <c>clientId</c>, <c>connectionId</c>, <c>customerId</c>,
/// <c>template.name</c>, <c>template.language</c> y <c>parameters.&lt;clave&gt;</c>.</summary>
public sealed class StartConversationValidator : AbstractValidator<StartConversationCommand>
{
    public StartConversationValidator()
    {
        RuleFor(command => command.ClientId).NotNull().NotEqual(Guid.Empty).OverridePropertyName("clientId");
        RuleFor(command => command.ConnectionId).NotNull().NotEqual(Guid.Empty).OverridePropertyName("connectionId");
        RuleFor(command => command.CustomerId).NotNull().NotEqual(Guid.Empty).OverridePropertyName("customerId");
        RuleFor(command => command.TemplateName).NotEmpty().MaximumLength(SendTemplateMessageValidator.NameMaxLength).OverridePropertyName("template.name");
        RuleFor(command => command.TemplateLanguage).NotEmpty().MaximumLength(SendTemplateMessageValidator.LanguageMaxLength).OverridePropertyName("template.language");
        RuleFor(command => command.Parameters).Custom((parameters, context) =>
        {
            foreach (var failure in TemplateParameterRules.Check(parameters))
            {
                context.AddFailure(failure);
            }
        });
    }
}

/// <summary>
/// Spec 2026-10-11 §5.2 y decisión 9: validador → tenant, permiso y módulo → membresía → cliente (404 si no existe o está
/// inactivo; 422 si no tiene teléfono ni BSUID) → conexión Active → plantilla del catálogo → la conversación del cliente en esa
/// conexión (<see cref="IConversationOpener"/>, asignada a quien llama si no existía) → el envío de la plantilla de
/// <see cref="TemplateOutbound"/>. La plantilla se valida antes de abrir: una inválida no deja una conversación vacía.
/// </summary>
public sealed class StartConversationHandler(
    IConversationQueries conversations,
    IConversationRepository repository,
    IMessageQueries messages,
    IOutboundMessages outbound,
    IWhatsAppCloudClient meta,
    IMessagingConnectionDirectory connections,
    IMessagingMemberNames memberNames,
    IMembershipDirectory membershipDirectory,
    IMessageTemplates templates,
    IMessagingCustomerDirectory customers,
    IConversationOpener opener,
    ConversationSummaryBuilder summaries,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IMessagingNotifier notifier,
    ReadReceipts receipts,
    IValidator<StartConversationCommand> validator)
    : ICommandHandler<StartConversationCommand, StartConversationResult>
{
    // CS9124: los parámetros que van a la guarda y al envío no se usan en los métodos; todo se pide al pegamento.
    private readonly TemplateOutbound _template = new(
        new OutboundGuards(conversations, repository, messages, outbound, connections, memberNames, membershipDirectory, tenantModules, executionContext, clock, notifier, receipts),
        meta, templates, customers);

    public async Task<StartConversationResult> HandleAsync(StartConversationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var tenantId = command.TenantId;
        var connectionId = command.ConnectionId!.Value;
        var member = await _template.Guards.AuthorizeAsync(tenantId, cancellationToken);

        // Spec 2026-10-14 §3: en Instagram no se inicia una conversación (sólo se responde). Antes del cliente y de la conexión Active:
        // un cliente que sólo tiene Instagram no es «inalcanzable», y una conexión pausada de Instagram tampoco es «no disponible».
        if (await connections.FindChannelAsync(tenantId, connectionId, cancellationToken) == ConversationChannel.Instagram)
        {
            throw new MessagingDomainException(MessagingErrorCodes.ChannelUnsupported, "A conversation cannot be started on Instagram.");
        }

        // 404 si no existe en este tenant o está inactivo (no se le escribe a un cliente dado de baja); 422 si no hay a dónde.
        var contact = await customers.FindContactAsync(tenantId, command.CustomerId!.Value, cancellationToken);
        if (contact is not { IsActive: true })
        {
            throw new ResourceNotFoundException(MessagingErrorCodes.CustomerNotFound, $"Customer '{command.CustomerId}' was not found.");
        }

        if (contact.PhoneE164 is null && contact.WhatsAppUserId is null)
        {
            throw new MessagingDomainException(MessagingErrorCodes.CustomerUnreachable, "The customer has neither a phone number nor a WhatsApp user id.");
        }

        // La conexión Active del tenant y la plantilla de su catálogo, antes de abrir nada: un 422 aquí no deja una conversación vacía.
        if (await connections.ResolveSenderAsync(tenantId, connectionId, cancellationToken) is null)
        {
            throw new MessagingDomainException(MessagingErrorCodes.ConnectionUnavailable, "The WhatsApp connection is paused, needs attention or was deleted.");
        }

        var resolved = await _template.ResolveAsync(tenantId, connectionId, command.TemplateName!, command.TemplateLanguage!, command.Parameters!, cancellationToken);
        var opened = await opener.OpenAsync(tenantId, connectionId, contact, member, executionContext.SubjectId, clock.UtcNow, cancellationToken);
        var row = await conversations.FindAsync(tenantId, opened.ConversationId, cancellationToken)
            ?? throw MessagingNotFound.Conversation(opened.ConversationId);
        var target = new OutboundTarget(row, member);

        var message = await _template.FindAlreadySentAsync(tenantId, target, command.ClientId!.Value, cancellationToken)
            ?? await _template.SendAsync(tenantId, target, command.ClientId.Value, resolved, cancellationToken);
        // La foto de la conversación después del envío (preview, estado del último mensaje, asignado).
        var fresh = await conversations.FindAsync(tenantId, opened.ConversationId, cancellationToken) ?? row;
        var summary = (await summaries.BuildAsync(tenantId, [fresh], cancellationToken))[0];
        return new StartConversationResult(new StartedConversationDto(summary, message), opened.Created);
    }
}
