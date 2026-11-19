using BuildingBlocks.Application;
using FluentValidation;
using Modules.Automations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Automations.Application;

// Spec 2026-11-18: subir el archivo de un bloque «Mensaje». Dos pasos más el PUT del navegador: la sesión (que revisa tipo y tope antes de
// emitir nada) y completar (que Storage revisa, comprime si hace falta y publica). Los autoriza sólo automations.chatbot.manage, nunca
// storage.file.*: quien arma bots sin permisos de archivos puede poner una imagen. No llevan If-Match: el chatbot no cambia (el borrador
// guarda el id con su autoguardado).

public sealed record CreateChatbotMediaSessionCommand(Guid TenantId, Guid ChatbotId, string? FileName, string? ContentType, long SizeBytes)
    : ICommand<ChatbotMediaSessionDto>;

public sealed record CompleteChatbotMediaCommand(Guid TenantId, Guid ChatbotId, Guid FileId) : ICommand<ChatbotMediaFileDto>;

/// <summary>BFF: <c>headers</c> son exactamente las cabeceras que firmó el PUT; el front las manda tal cual.</summary>
public sealed record ChatbotMediaSessionDto(Guid FileId, string UploadUrl, IReadOnlyDictionary<string, string> Headers);

/// <summary><c>kind</c> en minúscula; <c>url</c> es la URL pública absoluta. Tras comprimir una imagen, <c>fileName</c> termina en
/// <c>.jpg</c> y <c>contentType</c> es <c>image/jpeg</c>.</summary>
public sealed record ChatbotMediaFileDto(Guid FileId, string Kind, string FileName, string ContentType, long SizeBytes, string Url)
{
    public static ChatbotMediaFileDto From(ChatbotMediaFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return new(file.FileId, file.Kind, file.FileName, file.ContentType, file.SizeBytes, file.Url.AbsoluteUri);
    }
}

/// <summary>Lo que ve la persona en cada error de un archivo de chatbot (en <c>errors</c>, con el campo del error).</summary>
public static class ChatbotMediaMessages
{
    public const string UnsupportedType = "Ese tipo de archivo no se puede enviar. Usa JPG, PNG, MP4, 3GP, MP3, M4A, AAC, AMR, OGG, PDF, Word o Excel.";
    public const string TooLarge = "El archivo supera el tamaño máximo para su tipo.";
    public const string NotUploaded = "El archivo todavía no terminó de subir. Intenta de nuevo.";
    public const string NotFound = "No encontramos el archivo. Vuelve a subirlo.";
}

/// <summary>Forma del cuerpo, con las claves del JSON en <c>errors</c> (<c>OverridePropertyName</c>), como el de la imagen de encabezado
/// de una plantilla. Si el tipo se puede mandar y cuánto pesa lo decide el handler con la política.</summary>
public sealed class CreateChatbotMediaSessionValidator : AbstractValidator<CreateChatbotMediaSessionCommand>
{
    /// <summary>El mismo tope que el nombre de un adjunto de Mensajería.</summary>
    public const int MaxFileNameLength = 256;

    public CreateChatbotMediaSessionValidator()
    {
        RuleFor(command => command.FileName)
            .NotEmpty().WithMessage("Escribe el nombre del archivo.")
            .MaximumLength(MaxFileNameLength).WithMessage("El nombre del archivo es demasiado largo.")
            .OverridePropertyName("fileName");
        RuleFor(command => command.ContentType).NotEmpty().WithMessage("Indica el tipo del archivo.").OverridePropertyName("contentType");
        RuleFor(command => command.SizeBytes).GreaterThan(0).WithMessage("El archivo está vacío.").OverridePropertyName("sizeBytes");
    }
}

/// <summary>
/// Firma el PUT del archivo. Orden: autorización, validador, el chatbot existe en el tenant, tipo y tope de la política. El 422 de tipo o
/// tamaño llega antes del PUT, no después de subir 100 MB. A Storage va el MIME canónico de la regla y su tope, no lo declarado.
/// </summary>
public sealed class CreateChatbotMediaSessionHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IChatbotRepository chatbots,
    IChatbotMediaPolicy policy,
    IChatbotMediaStore store,
    IValidator<CreateChatbotMediaSessionCommand> validator)
    : ICommandHandler<CreateChatbotMediaSessionCommand, ChatbotMediaSessionDto>
{
    public async Task<ChatbotMediaSessionDto> HandleAsync(CreateChatbotMediaSessionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await AutomationsAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, AutomationsPermissions.ChatbotManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        _ = await chatbots.FindAsync(command.TenantId, command.ChatbotId, cancellationToken)
            ?? throw AutomationsNotFound.Chatbot(command.ChatbotId);

        var fileName = command.FileName!.Trim();
        var rule = policy.RuleFor(fileName);
        if (rule is null || !string.Equals(command.ContentType!.Trim(), rule.ContentType, StringComparison.OrdinalIgnoreCase))
        {
            throw ChatbotMediaErrors.UnsupportedType("fileName");
        }

        if (command.SizeBytes > rule.MaxBytes)
        {
            throw new AutomationsDomainException(
                AutomationsErrorCodes.MediaTooLarge,
                "The file exceeds the maximum size for its kind.",
                ChatbotMediaErrors.On("sizeBytes", ChatbotMediaMessages.TooLarge));
        }

        var session = await store.OpenUploadAsync(
            command.TenantId, fileName, rule.ContentType, command.SizeBytes, rule.MaxBytes, cancellationToken);
        return new ChatbotMediaSessionDto(session.FileId, session.UploadUrl.AbsoluteUri, session.Headers);
    }
}

/// <summary>
/// Confirma la subida: Storage revisa el contenido real, comprime una imagen grande y publica. Idempotente: completar dos veces responde
/// el mismo archivo. Un id de otro tenant es <c>not_found</c>, como uno que no existe.
/// </summary>
public sealed class CompleteChatbotMediaHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IChatbotRepository chatbots,
    IChatbotMediaStore store)
    : ICommandHandler<CompleteChatbotMediaCommand, ChatbotMediaFileDto>
{
    public async Task<ChatbotMediaFileDto> HandleAsync(CompleteChatbotMediaCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await AutomationsAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, AutomationsPermissions.ChatbotManage, cancellationToken);
        _ = await chatbots.FindAsync(command.TenantId, command.ChatbotId, cancellationToken)
            ?? throw AutomationsNotFound.Chatbot(command.ChatbotId);

        var completion = await store.CompleteAsync(command.TenantId, command.FileId, cancellationToken);
        return completion switch
        {
            { State: ChatbotMediaState.Available, File: { } file } => ChatbotMediaFileDto.From(file),
            { State: ChatbotMediaState.NotUploaded } => throw new AutomationsDomainException(
                AutomationsErrorCodes.MediaNotUploaded,
                "The file has not been uploaded yet.",
                ChatbotMediaErrors.On("fileId", ChatbotMediaMessages.NotUploaded)),
            { State: ChatbotMediaState.Rejected } => throw ChatbotMediaErrors.UnsupportedType("fileId"),
            _ => throw new AutomationsDomainException(
                AutomationsErrorCodes.MediaNotFound,
                "The chatbot media file was not found.",
                ChatbotMediaErrors.On("fileId", ChatbotMediaMessages.NotFound)),
        };
    }
}

internal static class ChatbotMediaErrors
{
    public static AutomationsDomainException UnsupportedType(string field) =>
        new(AutomationsErrorCodes.MediaUnsupportedType, "This file type cannot be sent by a chatbot.", On(field, ChatbotMediaMessages.UnsupportedType));

    public static IReadOnlyDictionary<string, string[]> On(string field, string message) =>
        new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] };
}
