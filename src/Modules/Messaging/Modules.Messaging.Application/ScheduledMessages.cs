using System.Security.Cryptography;
using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-11-09 «Contrato HTTP»: el mensaje programado tal como lo pinta la pantalla.</summary>
public sealed record ScheduledMessageDto(
    Guid Id,
    Guid ConversationId,
    Guid ClientId,
    string Status,
    DateTimeOffset ScheduledAt,
    string Kind,
    bool IsVoiceNote,
    string? Text,
    string? Caption,
    ScheduledMediaDto? Media,
    MemberRefDto Author,
    MemberRefDto CreatedBy,
    MemberRefDto? UpdatedBy,
    DateTimeOffset? UpdatedAt,
    string? FailureCode,
    bool CanRetry,
    Guid? SentMessageId,
    long Version);

public sealed record ScheduledMediaDto(string MimeType, string? FileName, long SizeBytes);

/// <summary><c>GET …/scheduled-message</c>: <c>null</c> cuando la conversación no tiene un programado activo.</summary>
public sealed record ScheduledMessageEnvelopeDto(ScheduledMessageDto? ScheduledMessage);

/// <summary><paramref name="Created"/> distingue el 201 del 200 de un <c>clientId</c> que ya existía.</summary>
public sealed record ScheduleMessageResult(bool Created, ScheduledMessageDto Body);

/// <summary>
/// El único camino del programado a su DTO. <c>CanRetry</c> lo decide el backend (BFF): la pantalla no puede saber sola si la ventana
/// de 24 h sigue abierta con el margen de seguridad, ni si el contacto se bloqueó después; calcularlo en el navegador con su reloj
/// ofrecería «Reintentar» a un mensaje que el ejecutor volvería a rechazar.
/// </summary>
public sealed class ScheduledMessageMapper(IMessagingMemberNames memberNames)
{
    public async Task<ScheduledMessageDto> ToDtoAsync(
        Guid tenantId, ScheduledMessage message, ConversationRow conversation, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(conversation);
        var ids = new List<Guid> { message.AuthorMemberId, message.CreatedByMemberId };
        if (message.UpdatedByMemberId is { } updater)
        {
            ids.Add(updater);
        }

        var names = await memberNames.FindAsync(tenantId, ids, cancellationToken);
        MemberRefDto Ref(Guid id) => new(id, names.GetValueOrDefault(id) ?? MessageMapping.DeletedMemberName);

        var media = message.MediaStorageKey is not null && message.MediaMimeType is { } mimeType
            ? new ScheduledMediaDto(mimeType, message.MediaFileName, message.MediaSizeBytes ?? 0)
            : null;
        var canRetry = message.Status == ScheduledMessageStatus.Failed
            && ScheduledMessage.IsWindowOpenForSending(conversation.LastInboundAt, now)
            && conversation.BlockedAt is null;
        return new ScheduledMessageDto(
            message.Id, message.ConversationId, message.ClientId, message.Status.ToString(), message.ScheduledAt, message.Kind.ToString(),
            message.IsVoiceNote, message.Text, message.Caption, media, Ref(message.AuthorMemberId), Ref(message.CreatedByMemberId),
            message.UpdatedByMemberId is { } updatedBy ? Ref(updatedBy) : null, message.UpdatedAt, message.FailureCode, canRetry,
            message.SentMessageId, message.Version);
    }
}

/// <summary>Spec 2026-11-09 «Contrato HTTP»: <c>text</c> o <c>uploadId</c>, nunca los dos ni ninguno. Sin <c>replyTo</c>: un
/// programado no se cita (decisión del owner).</summary>
public sealed record CreateScheduledMessageCommand(
    Guid TenantId, Guid ConversationId, Guid? ClientId, DateTimeOffset? ScheduledAt, string? Text, Guid? UploadId, string? FileName, string? Caption)
    : ICommand<ScheduleMessageResult>;

/// <summary>Forma del cuerpo, con las reglas de <see cref="SendMessageValidator"/> y <see cref="SendMediaMessageValidator"/> pero midiendo
/// <c>{saludo}</c> como lo más largo que puede resolverse (<see cref="ScheduledText.MeasuredLength"/>): el saludo se resuelve al enviar,
/// horas después, y el tope de Meta es el del texto ya resuelto. Las claves de <c>errors</c> son las del JSON.</summary>
public sealed class CreateScheduledMessageValidator : AbstractValidator<CreateScheduledMessageCommand>
{
    public CreateScheduledMessageValidator()
    {
        RuleFor(command => command.ClientId).NotNull().NotEqual(Guid.Empty).OverridePropertyName("clientId");
        RuleFor(command => command.ScheduledAt).NotNull().OverridePropertyName("scheduledAt");
        RuleFor(command => command)
            .Must(command => (command.Text is null) != (command.UploadId is null))
            .WithMessage("Escribe un mensaje o adjunta un archivo, no los dos.")
            .OverridePropertyName("text");

        RuleFor(command => command.Text)
            .Must(IsAcceptableMeasuredText)
            .When(command => command.Text is not null)
            .WithMessage("Escribe un mensaje de hasta 4096 caracteres.")
            .OverridePropertyName("text");

        RuleFor(command => command.UploadId).NotEqual(Guid.Empty).When(command => command.UploadId is not null).OverridePropertyName("uploadId");
        RuleFor(command => command.FileName)
            .NotEmpty().WithMessage("Escribe el nombre del archivo.")
            .MaximumLength(CreateMediaUploadSessionValidator.MaxFileNameLength).WithMessage("El nombre del archivo es demasiado largo.")
            .When(command => command.UploadId is not null)
            .OverridePropertyName("fileName");
        RuleFor(command => command.Caption)
            .Must(caption => ScheduledText.MeasuredLength(caption!.Trim()) <= SendMediaMessageValidator.MaxCaptionLength)
            .When(command => command.UploadId is not null && command.Caption is not null)
            .WithMessage("El texto que acompaña al archivo puede tener hasta 1024 caracteres.")
            .OverridePropertyName("caption");
        RuleFor(command => command.Caption)
            .Must(caption => string.IsNullOrWhiteSpace(caption))
            .When(command => command.UploadId is not null && !string.IsNullOrWhiteSpace(command.FileName) && SendMediaMessageValidator.IsAudio(command.FileName))
            .WithMessage("Los audios se envían sin texto.")
            .OverridePropertyName("caption");
    }

    private static bool IsAcceptableMeasuredText(string? text) =>
        SendMessageValidator.IsAcceptableText(text) && ScheduledText.MeasuredLength(text!.Trim()) <= SendMessageValidator.TextMaxLength;
}

/// <summary>
/// Spec 2026-11-09 «Orden del POST»: validador → tenant, permiso y conversación (<see cref="OutboundGuards.LoadAsync"/>) → el
/// <c>clientId</c> ya guardado (200 con esa fila, en el estado que tenga, sin hora, admisión ni staging) → hora → un solo activo →
/// canal → admisión → adjunto → guardar. La admisión autoasigna, así que todo lo que puede fallar por validación va antes de ella.
/// El staging se borra sólo cuando la fila ya quedó guardada: un 409 no le hace perder la subida a quien la volverá a usar.
/// </summary>
public sealed class CreateScheduledMessageHandler(
    IScheduledMessageRepository scheduled,
    IConversationQueries conversations,
    IConversationRepository repository,
    IMessageQueries messages,
    IOutboundMessages outbound,
    IWhatsAppCloudClient meta,
    IMessagingMediaStore store,
    IMessagingUploadStaging staging,
    IMessagingImageCompressor compressor,
    IMessagingConnectionDirectory connections,
    IMessagingMemberNames memberNames,
    IMembershipDirectory membershipDirectory,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IMessagingNotifier notifier,
    ReadReceipts receipts,
    IAutomationsChatbotNames chatbotNames,
    IMessagingAuditRecorder audit,
    ScheduledMessageMapper mapper,
    IValidator<CreateScheduledMessageCommand> validator)
    : ICommandHandler<CreateScheduledMessageCommand, ScheduleMessageResult>
{
    // Propiedades y no campos: los parámetros que van a la guarda y al preparador también se usan en los métodos (CS9124).
    private OutboundGuards Guards => new(
        conversations, repository, messages, outbound, connections, memberNames, membershipDirectory, tenantModules, executionContext, clock, notifier, receipts, chatbotNames);

    private OutboundMediaPreparer Preparer => new(meta, store, compressor);

    private ScheduledMessageWriter Writer => new(scheduled, store, executionContext, audit, clock, mapper);

    public async Task<ScheduleMessageResult> HandleAsync(CreateScheduledMessageCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var guards = Guards;
        var target = await guards.LoadAsync(command.TenantId, command.ConversationId, cancellationToken);
        var conversation = target.Conversation;
        var clientId = command.ClientId!.Value;

        if (await scheduled.FindByClientIdAsync(command.TenantId, conversation.Id, clientId, cancellationToken) is { } existing)
        {
            return await ExistingAsync(command.TenantId, existing, conversation, cancellationToken);
        }

        var now = clock.UtcNow;
        var scheduledAt = command.ScheduledAt!.Value;
        ScheduledMessage.EnsureSchedulable(scheduledAt, conversation.LastInboundAt, now);

        // Un solo activo por conversación: se mira antes de admitir para que un request condenado a 409 no autoasigne.
        if (await scheduled.FindActiveAsync(command.TenantId, conversation.Id, cancellationToken) is not null)
        {
            throw ScheduledMessageWriter.AlreadyExists();
        }

        return command.UploadId is null
            ? await ScheduleTextAsync(command, guards, target, scheduledAt, now, cancellationToken)
            : await ScheduleMediaAsync(command, guards, target, scheduledAt, now, cancellationToken);
    }

    private async Task<ScheduleMessageResult> ScheduleTextAsync(
        CreateScheduledMessageCommand command, OutboundGuards guards, OutboundTarget target, DateTimeOffset scheduledAt, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var conversation = target.Conversation;
        var text = command.Text!.Trim();

        // Mismo tope y mismo mensaje que SendMessageHandler, pero con {saludo} contado como 13: se resuelve al enviar.
        if (conversation.Channel == ConversationChannel.Instagram && ScheduledText.MeasuredLength(text) > SendMessageValidator.InstagramTextMaxLength)
        {
            throw new ValidationException([new ValidationFailure("text", "Escribe un mensaje de hasta 1000 caracteres: es el máximo de Instagram.")]);
        }

        await guards.AdmitAsync(command.TenantId, target, replyTo: null, cancellationToken);
        var message = ScheduledMessage.CreateText(
            Guid.CreateVersion7(), command.TenantId, conversation.Id, command.ClientId!.Value, text, scheduledAt, target.Member, now);
        return await SaveAsync(command.TenantId, message, conversation, now, scheduledKey: null, stagingKey: null, cancellationToken);
    }

    private async Task<ScheduleMessageResult> ScheduleMediaAsync(
        CreateScheduledMessageCommand command, OutboundGuards guards, OutboundTarget target, DateTimeOffset scheduledAt, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var conversation = target.Conversation;
        var clientId = command.ClientId!.Value;

        // Como SendMediaMessageHandler: una extensión que ningún canal acepta no se mide contra R2 ni cambia el dueño.
        var fileName = Path.GetFileName(command.FileName!.Trim());
        var extension = OutboundMediaPolicy.ExtensionOf(fileName);
        if (OutboundMediaPolicy.FormatOf(extension) is not { } declared)
        {
            throw new MessagingDomainException(MessagingErrorCodes.MediaUnsupportedFormat, "No channel accepts this file type as is.");
        }

        var channel = conversation.Channel;
        if (!OutboundMediaPolicy.IsAllowed(declared, channel))
        {
            throw new MessagingDomainException(MessagingErrorCodes.MediaUnsupportedFormat, "The channel does not accept this file type as is.");
        }

        await guards.AdmitAsync(command.TenantId, target, replyTo: null, cancellationToken);

        var stagingKey = MessagingMediaKeys.Staging(command.TenantId, conversation.Id, target.Member, command.UploadId!.Value, extension);
        var staged = await staging.StatAsync(stagingKey, cancellationToken);
        if (staged is null)
        {
            // Dos requests con el mismo clientId: el primero ya guardó y consumió el staging. Gana la idempotencia, no el 422.
            return await scheduled.FindByClientIdAsync(command.TenantId, conversation.Id, clientId, cancellationToken) is { } done
                ? await ExistingAsync(command.TenantId, done, conversation, cancellationToken)
                : throw OutboundMediaPreparer.UploadNotFound();
        }

        if (staged.SizeBytes <= 0 || staged.SizeBytes > OutboundMediaPolicy.MaxBytesOf(declared, channel)!.Value)
        {
            await DiscardAsync(stagingKey);
            throw new MessagingDomainException(MessagingErrorCodes.MediaTooLarge, "The file is bigger than the channel allows for its type.");
        }

        // Sin remitente: no se sube a Meta ahora (el ejecutor lo hace al enviar). Sólo se comprueba el contenido y se comprime la imagen.
        var prepared = await Preparer.PrepareAsync(
            null, stagingKey, staged.SizeBytes, fileName, extension, () => DiscardAsync(stagingKey), cancellationToken);

        var messageId = Guid.CreateVersion7();
        var scheduledKey = MessagingMediaKeys.Scheduled(command.TenantId, messageId, prepared.Compressed is null ? extension : ".jpg");
        try
        {
            if (prepared.Compressed is { } bytes)
            {
                using var content = new MemoryStream(bytes, writable: false);
                await store.UploadAsync(scheduledKey, content, bytes.Length, OutboundMediaPreparer.ImageMimeType, cancellationToken);
            }
            else
            {
                await staging.PromoteAsync(stagingKey, scheduledKey, staged.ETag, cancellationToken);
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            await DiscardAsync(scheduledKey);
            throw new MessagingDomainException(MessagingErrorCodes.MediaUploadFailed, "The attachment could not be stored.", exception);
        }

        // La cantidad y el tipo que se guardan son los del objeto ya guardado (la comprimida, no el original).
        var message = ScheduledMessage.CreateMedia(
            messageId, command.TenantId, conversation.Id, clientId, OutboundMediaPolicy.KindOf(declared), isVoiceNote: false, command.Caption,
            new ScheduledMedia(scheduledKey, prepared.MimeType, prepared.FileName, prepared.SizeBytes, prepared.Sha256Hex), scheduledAt, target.Member, now);
        return await SaveAsync(command.TenantId, message, conversation, now, scheduledKey, stagingKey, cancellationToken);
    }

    private Task<ScheduleMessageResult> SaveAsync(
        Guid tenantId, ScheduledMessage message, ConversationRow conversation, DateTimeOffset now, string? scheduledKey, string? stagingKey,
        CancellationToken cancellationToken) =>
        Writer.SaveAsync(tenantId, message, conversation, now, scheduledKey, stagingKey, cancellationToken);

    private Task<ScheduleMessageResult> ExistingAsync(
        Guid tenantId, ScheduledMessage existing, ConversationRow conversation, CancellationToken cancellationToken) =>
        Writer.ExistingAsync(tenantId, existing, conversation, cancellationToken);

    private Task DiscardAsync(string key) => ScheduledMessageWriter.DiscardAsync(store, key);
}

/// <summary>
/// El tramo que comparten crear un programado de texto o adjunto y crear una nota de voz: guardar la fila con su auditoría, resolver la
/// carrera del <c>clientId</c> y devolver el ya guardado. Una sola copia para que los dos caminos no se separen.
/// </summary>
internal sealed class ScheduledMessageWriter(
    IScheduledMessageRepository scheduled,
    IMessagingMediaStore store,
    IExecutionContext executionContext,
    IMessagingAuditRecorder audit,
    IClock clock,
    ScheduledMessageMapper mapper)
{
    public async Task<ScheduleMessageResult> SaveAsync(
        Guid tenantId, ScheduledMessage message, ConversationRow conversation, DateTimeOffset now, string? scheduledKey, string? stagingKey,
        CancellationToken cancellationToken)
    {
        audit.RecordScheduledMessage(
            tenantId, executionContext.SubjectId, bySystem: false, MessagingAuditActions.ScheduledMessageCreated, message.Id, now);
        try
        {
            await scheduled.AddAsync(message, cancellationToken);
        }
        catch (Exception exception)
        {
            // La copia del adjunto es de esta fila; ni la ganadora de una carrera ni nadie más la usa.
            if (scheduledKey is not null)
            {
                await DiscardAsync(store, scheduledKey);
            }

            // Dos requests con el mismo clientId a la vez: el índice dejó pasar uno. El otro devuelve lo que ganó, no un 409.
            if (exception is ResourceConflictException { Code: MessagingErrorCodes.ScheduledAlreadyExists }
                && await scheduled.FindByClientIdAsync(tenantId, conversation.Id, message.ClientId, cancellationToken) is { } winner)
            {
                return await ExistingAsync(tenantId, winner, conversation, cancellationToken);
            }

            throw;
        }

        // Guardado: ahora sí el uploadId se consume. Best effort, el lifecycle del bucket limpia el resto.
        if (stagingKey is not null)
        {
            await DiscardAsync(store, stagingKey);
        }

        return new ScheduleMessageResult(true, await mapper.ToDtoAsync(tenantId, message, conversation, now, cancellationToken));
    }

    public async Task<ScheduleMessageResult> ExistingAsync(
        Guid tenantId, ScheduledMessage existing, ConversationRow conversation, CancellationToken cancellationToken) =>
        new(false, await mapper.ToDtoAsync(tenantId, existing, conversation, clock.UtcNow, cancellationToken));

    internal static ResourceConflictException AlreadyExists() =>
        new(MessagingErrorCodes.ScheduledAlreadyExists, "The conversation already has a scheduled message.");

    // Best effort: si borrar falla queda un objeto en un bucket privado que nadie puede pedir y el lifecycle lo limpia.
    internal static async Task DiscardAsync(IMessagingMediaStore store, string key)
    {
        try
        {
            await store.DeleteAsync(key, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Ver comentario.
        }
    }
}

/// <summary>Spec 2026-11-09 «Contrato HTTP»: <c>POST …/scheduled-message/voice-note</c>, multipart con <c>file</c>. Una nota de voz nunca
/// lleva pie de foto (decisión del owner) ni <c>replyTo</c>.</summary>
public sealed record CreateScheduledVoiceNoteCommand(
    Guid TenantId, Guid ConversationId, Guid? ClientId, DateTimeOffset? ScheduledAt, byte[]? Audio) : ICommand<ScheduleMessageResult>;

/// <summary>Las reglas de <see cref="SendAudioMessageValidator"/> (<c>clientId</c>; <c>file</c> de 1 B a su tope y OGG/Opus por contenido)
/// más <c>scheduledAt</c> obligatorio.</summary>
public sealed class CreateScheduledVoiceNoteValidator : AbstractValidator<CreateScheduledVoiceNoteCommand>
{
    public CreateScheduledVoiceNoteValidator()
    {
        RuleFor(command => command.ClientId).NotNull().NotEqual(Guid.Empty).OverridePropertyName("clientId");
        RuleFor(command => command.ScheduledAt).NotNull().OverridePropertyName("scheduledAt");
        RuleFor(command => command.Audio)
            .Cascade(CascadeMode.Stop)
            .Must(audio => audio is { Length: > 0 and <= SendAudioMessageValidator.MaxAudioBytes }).WithMessage("Graba una nota de voz de hasta 16 MB.")
            .Must(audio => OggOpusSniffer.IsOggOpus(audio!)).WithMessage("La nota de voz no está en el formato que admite WhatsApp. Graba de nuevo.")
            .OverridePropertyName("file");
    }
}

/// <summary>
/// Mismo orden que <see cref="CreateScheduledMessageHandler"/>: validador → tenant, permiso y conversación → el <c>clientId</c> ya guardado
/// (200 sin transcodificar, admitir ni subir) → hora → un solo activo → Instagram a WAV (un 422 del campo no debe cambiar el dueño) →
/// admisión → R2 → guardar. Se guarda lo que se enviará: OGG/Opus, o el WAV en Instagram, con <c>kind = Audio</c>, nota de voz, sin nombre
/// de archivo ni pie.
/// </summary>
public sealed class CreateScheduledVoiceNoteHandler(
    IScheduledMessageRepository scheduled,
    IConversationQueries conversations,
    IConversationRepository repository,
    IMessageQueries messages,
    IOutboundMessages outbound,
    IMessagingMediaStore store,
    IOpusTranscoder transcoder,
    IMessagingConnectionDirectory connections,
    IMessagingMemberNames memberNames,
    IMembershipDirectory membershipDirectory,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IMessagingNotifier notifier,
    ReadReceipts receipts,
    IAutomationsChatbotNames chatbotNames,
    IMessagingAuditRecorder audit,
    ScheduledMessageMapper mapper,
    IValidator<CreateScheduledVoiceNoteCommand> validator)
    : ICommandHandler<CreateScheduledVoiceNoteCommand, ScheduleMessageResult>
{
    // Propiedades y no campos: los parámetros que van a la guarda y al escritor también se usan en los métodos (CS9124).
    private OutboundGuards Guards => new(
        conversations, repository, messages, outbound, connections, memberNames, membershipDirectory, tenantModules, executionContext, clock, notifier, receipts, chatbotNames);

    private ScheduledMessageWriter Writer => new(scheduled, store, executionContext, audit, clock, mapper);

    public async Task<ScheduleMessageResult> HandleAsync(CreateScheduledVoiceNoteCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var guards = Guards;
        var target = await guards.LoadAsync(command.TenantId, command.ConversationId, cancellationToken);
        var conversation = target.Conversation;
        var clientId = command.ClientId!.Value;

        if (await scheduled.FindByClientIdAsync(command.TenantId, conversation.Id, clientId, cancellationToken) is { } existing)
        {
            return await Writer.ExistingAsync(command.TenantId, existing, conversation, cancellationToken);
        }

        var now = clock.UtcNow;
        var scheduledAt = command.ScheduledAt!.Value;
        ScheduledMessage.EnsureSchedulable(scheduledAt, conversation.LastInboundAt, now);

        if (await scheduled.FindActiveAsync(command.TenantId, conversation.Id, cancellationToken) is not null)
        {
            throw ScheduledMessageWriter.AlreadyExists();
        }

        // Como SendAudioMessageHandler: Instagram no acepta Opus, y una grabación que no decodifica es un 422 antes de admitir.
        var audio = command.Audio!;
        var mimeType = OggOpusSniffer.MimeType;
        var extension = ".ogg";
        if (conversation.Channel == ConversationChannel.Instagram)
        {
            audio = InstagramVoiceNote.Transcode(transcoder, audio);
            mimeType = InstagramAudio.WavMimeType;
            extension = ".wav";
        }

        await guards.AdmitAsync(command.TenantId, target, replyTo: null, cancellationToken);

        var messageId = Guid.CreateVersion7();
        var key = MessagingMediaKeys.Scheduled(command.TenantId, messageId, extension);
        try
        {
            using var content = new MemoryStream(audio, writable: false);
            await store.UploadAsync(key, content, audio.Length, mimeType, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            await ScheduledMessageWriter.DiscardAsync(store, key);
            throw new MessagingDomainException(MessagingErrorCodes.MediaUploadFailed, "The voice note could not be stored.", exception);
        }

        var message = ScheduledMessage.CreateMedia(
            messageId, command.TenantId, conversation.Id, clientId, MessageKind.Audio, isVoiceNote: true, caption: null,
            new ScheduledMedia(key, mimeType, FileName: null, audio.Length, Convert.ToHexStringLower(SHA256.HashData(audio))), scheduledAt, target.Member, now);
        return await Writer.SaveAsync(command.TenantId, message, conversation, now, key, stagingKey: null, cancellationToken);
    }
}

/// <summary><c>GET …/scheduled-message</c>.</summary>
public sealed record GetScheduledMessageQuery(Guid TenantId, Guid ConversationId) : IQuery<ScheduledMessageEnvelopeDto>;

public sealed class GetScheduledMessageHandler(
    IScheduledMessageRepository scheduled,
    IConversationQueries conversations,
    ScheduledMessageMapper mapper,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock)
    : IQueryHandler<GetScheduledMessageQuery, ScheduledMessageEnvelopeDto>
{
    public async Task<ScheduledMessageEnvelopeDto> HandleAsync(GetScheduledMessageQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, MessagingPermissions.ConversationRead, cancellationToken);
        var conversation = await conversations.FindAsync(query.TenantId, query.ConversationId, cancellationToken)
            ?? throw MessagingNotFound.Conversation(query.ConversationId);
        var active = await scheduled.FindActiveAsync(query.TenantId, conversation.Id, cancellationToken);
        return new ScheduledMessageEnvelopeDto(
            active is null ? null : await mapper.ToDtoAsync(query.TenantId, active, conversation, clock.UtcNow, cancellationToken));
    }
}
