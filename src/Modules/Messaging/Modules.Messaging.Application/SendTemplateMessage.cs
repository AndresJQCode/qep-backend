using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-10-11 §5.2: las reglas de Meta sobre el valor de un parámetro de plantilla (error 132018), validadas acá para que el
/// 422 diga el campo exacto (<c>parameters.first_name</c>) en lugar de una falla tardía de Meta. Las comparten el envío a una
/// conversación, la conversación nueva y el formulario de campañas.
/// </summary>
public static class TemplateParameterRules
{
    public const int MaxValueLength = 1024;

    /// <summary>Meta rechaza más de 4 espacios seguidos.</summary>
    public const int MaxConsecutiveSpaces = 4;

    public const string KeyPrefix = "parameters.";

    public static IEnumerable<ValidationFailure> Check(IReadOnlyDictionary<string, string?>? parameters)
    {
        if (parameters is null)
        {
            yield return new ValidationFailure("parameters", "Envía las variables de la plantilla.");
            yield break;
        }

        foreach (var (key, value) in parameters)
        {
            if (!IsValid(value))
            {
                yield return new ValidationFailure(
                    KeyPrefix + key,
                    $"El valor de «{key}» debe tener de 1 a {MaxValueLength} caracteres, sin saltos de línea ni tabulaciones ni más de {MaxConsecutiveSpaces} espacios seguidos.");
            }
        }
    }

    public static bool IsValid(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaxValueLength
        && !value.Contains('\n', StringComparison.Ordinal)
        && !value.Contains('\r', StringComparison.Ordinal)
        && !value.Contains('\t', StringComparison.Ordinal)
        && !value.Contains(new string(' ', MaxConsecutiveSpaces + 1), StringComparison.Ordinal);
}

public sealed record SendTemplateMessageCommand(
    Guid TenantId, Guid ConversationId, Guid? ClientId, string? Name, string? Language, IReadOnlyDictionary<string, string?>? Parameters) : ICommand<MessageDto>;

/// <summary><c>clientId</c> GUID no vacío; <c>name</c> y <c>language</c> de la plantilla; cada valor de <c>parameters</c> por las
/// reglas de Meta. Las claves de <c>errors</c> son las del cuerpo JSON (<c>OverridePropertyName</c>).</summary>
public sealed class SendTemplateMessageValidator : AbstractValidator<SendTemplateMessageCommand>
{
    public const int NameMaxLength = 512;
    public const int LanguageMaxLength = 16;

    public SendTemplateMessageValidator()
    {
        RuleFor(command => command.ClientId).NotNull().NotEqual(Guid.Empty).OverridePropertyName("clientId");
        RuleFor(command => command.Name).NotEmpty().MaximumLength(NameMaxLength).OverridePropertyName("name");
        RuleFor(command => command.Language).NotEmpty().MaximumLength(LanguageMaxLength).OverridePropertyName("language");
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
/// Spec 2026-10-11 §5.2: el orden del envío de texto (spec 2026-10-10 §8.5) con las mismas guardas (<see cref="OutboundGuards"/>)
/// pero sin exigir la ventana abierta —esa es la razón de existir de la plantilla—: validador → tenant, permiso y módulo →
/// conversación del tenant → membresía → lo ya enviado con ese <c>clientId</c> → plantilla del catálogo (aprobada, enviable y
/// con las claves exactas) → status Open → dueño (sin asignar, enviar es tomar) → reclamo → conexión Active → Meta → tabla de
/// respuestas. El pegamento común con <c>POST /conversations</c> vive en <see cref="TemplateOutbound"/>.
/// </summary>
public sealed class SendTemplateMessageHandler(
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
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IMessagingNotifier notifier,
    IValidator<SendTemplateMessageCommand> validator)
    : ICommandHandler<SendTemplateMessageCommand, MessageDto>
{
    // CS9124: los parámetros que van a la guarda y al envío no se usan en los métodos; todo se pide al pegamento.
    private readonly TemplateOutbound _template = new(
        new OutboundGuards(conversations, repository, messages, outbound, connections, memberNames, membershipDirectory, tenantModules, executionContext, clock, notifier),
        meta, templates, customers);

    public async Task<MessageDto> HandleAsync(SendTemplateMessageCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var target = await _template.Guards.LoadAsync(command.TenantId, command.ConversationId, cancellationToken);

        // Lo que ya salió, ya salió: antes de mirar el catálogo, el estado o el dueño.
        if (await _template.FindAlreadySentAsync(command.TenantId, target, command.ClientId!.Value, cancellationToken) is { } sent)
        {
            return sent;
        }

        var resolved = await _template.ResolveAsync(
            command.TenantId, target.Conversation.ConnectionId, command.Name!, command.Language!, command.Parameters!, cancellationToken);
        return await _template.SendAsync(command.TenantId, target, command.ClientId.Value, resolved, cancellationToken);
    }
}
