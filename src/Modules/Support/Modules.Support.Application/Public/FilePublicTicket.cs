using BuildingBlocks.Application;
using BuildingBlocks.Domain.Identification;
using FluentValidation;
using FluentValidation.Results;
using Modules.Support.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Support.Application;

/// <param name="VisitorIp">La IP del visitante (<c>RemoteIpAddress</c>, ya corregida por <c>X-Real-IP</c>): para Turnstile y para las
/// aceptaciones legales del camino con datos. Nunca se guarda para un anónimo.</param>
public sealed record FilePublicTicketCommand(
    string? TenantSlug,
    string VisitorIp,
    Guid? TicketTypeId,
    string? Subject,
    string? Description,
    bool Anonymous,
    SupportPublicRequesterRequest? Requester,
    bool AcceptsDataPolicy,
    int? PrivacyNoticeVersion,
    int? AnonymousNoticeVersion,
    bool AcceptsTerms,
    int? TermsVersion,
    int? FormVersion,
    string? TurnstileToken,
    IReadOnlyList<SupportPublicAttachmentRef>? Attachments = null)
    : ICommand<SupportPublicFiledDto>
{
    /// <summary>La versión vigente del formulario del tenant, con la que se juzgan los campos. No viaja por el cable: la pone el handler
    /// antes de validar.</summary>
    public SupportFormVersion? Form { get; init; }

    public static FilePublicTicketCommand From(string? tenantSlug, string visitorIp, SupportPublicFileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(
            tenantSlug,
            visitorIp,
            request.TicketTypeId,
            request.Subject,
            request.Description,
            request.Anonymous,
            request.Requester,
            request.AcceptsDataPolicy,
            request.PrivacyNoticeVersion,
            request.AnonymousNoticeVersion,
            request.AcceptsTerms,
            request.TermsVersion,
            request.FormVersion,
            request.TurnstileToken,
            request.Attachments);
    }
}

/// <summary>
/// El radicado del formulario público (plan F18 a F20; spec 2026-11-15 §9, plan L15 y L16). Qué campos se piden y cuáles son obligatorios lo dice
/// <see cref="SupportFormFields"/> con la versión del formulario: este validador no repite la regla, sólo la aplica. Un campo que el
/// formulario oculta no se valida (y el handler no lo guarda). Las claves del mapa <c>errors</c> son las del cuerpo. Mide los textos
/// recortados, como se guardan.
/// </summary>
public sealed class FilePublicTicketValidator : AbstractValidator<FilePublicTicketCommand>
{
    private const string Colombia = "CO";

    public FilePublicTicketValidator(ISupportCountries countries, ISupportPhoneNormalizer phones)
    {
        ArgumentNullException.ThrowIfNull(countries);
        ArgumentNullException.ThrowIfNull(phones);

        RuleFor(command => command.TicketTypeId).NotNull().NotEqual(Guid.Empty);
        RuleFor(command => command.Subject).MustFit(1, SupportTicket.SubjectMaxLength);
        RuleFor(command => command.Description).MustFit(1, SupportTicket.DescriptionMaxLength);
        RuleFor(command => command.FormVersion).NotNull();
        // Spec 2026-11-15 §9 (plan L15 y L16): los dos caminos aceptan los términos. Por HTTP sale como validation.failed con
        // errors.AcceptsTerms: ApiExceptionHandler no publica el ErrorCode.
        RuleFor(command => command.TermsVersion).NotNull();
        RuleFor(command => command.AcceptsTerms)
            .Equal(true)
            .WithErrorCode(SupportErrorCodes.PublicTermsRequired)
            .WithMessage("The terms and conditions must be accepted.");
        // Plan F34 y F35: hasta cinco, cada uno con su par completo y sin repetir. Que existan y estén disponibles lo mira el handler,
        // con la misma respuesta para todos los casos.
        RuleFor(command => command.Attachments)
            .Must(AttachmentsFit)
            .WithMessage(
                $"Up to {SupportPublicAttachmentRules.MaxFiles} different attachments, each with its fileId and uploadKey.");

        // Plan L16: cada camino manda la versión de su aviso y no la del otro. AcceptsDataPolicy se ignora en el anónimo.
        When(command => command.Anonymous, () =>
        {
            RuleFor(command => command.Requester)
                .Null()
                .WithMessage("An anonymous ticket cannot carry requester data.");
            RuleFor(command => command.AnonymousNoticeVersion).NotNull();
            RuleFor(command => command.PrivacyNoticeVersion)
                .Null()
                .WithMessage("An anonymous ticket does not accept the privacy notice.");
        });

        When(command => !command.Anonymous, () =>
        {
            RuleFor(command => command.Requester).NotNull();
            RuleFor(command => command.PrivacyNoticeVersion).NotNull();
            RuleFor(command => command.AnonymousNoticeVersion)
                .Null()
                .WithMessage("A ticket with requester data does not accept the anonymous notice.");
            RuleFor(command => command.AcceptsDataPolicy)
                .Equal(true)
                .WithErrorCode(SupportErrorCodes.PublicConsentRequired)
                .WithMessage("The data policy must be accepted.");
        });

        When(command => !command.Anonymous && command.Requester is not null, () =>
        {
            RuleFor(command => command.Requester!.DocumentType)
                .Must(text => !string.IsNullOrWhiteSpace(text))
                .When(command => Requires(command, SupportFormFields.DocumentType))
                .WithMessage("The document type is required.");
            // Con un país mal escrito el error es del país, no del tipo.
            RuleFor(command => command.Requester!.DocumentType)
                .Must((command, type) => DocumentTypesByCountry.IsAllowed(type, CountryOf(command)!))
                .When(command => Shows(command, SupportFormFields.DocumentType)
                    && !string.IsNullOrWhiteSpace(command.Requester!.DocumentType)
                    && HasKnownCountry(command, countries))
                .WithMessage(command => $"The document type is not allowed for country '{CountryOf(command)}'.");
            RuleFor(command => command.Requester!.DocumentNumber)
                .MustFit(1, TicketRequester.DocumentNumberMaxLength)
                .When(command => Requires(command, SupportFormFields.DocumentNumber));

            RuleFor(command => command.Requester!.FirstNames)
                .MustFit(1, TicketRequester.NameMaxLength)
                .When(command => Requires(command, SupportFormFields.FirstNames));
            RuleFor(command => command.Requester!.LastNames)
                .MustFit(1, TicketRequester.NameMaxLength)
                .When(command => Requires(command, SupportFormFields.LastNames));
            // La razón social es el único obligatorio que configura el tenant (BusinessNameRequired).
            RuleFor(command => command.Requester!.BusinessName)
                .MustFit(1, TicketRequester.NameMaxLength)
                .When(command => Requires(command, SupportFormFields.BusinessName));
            RuleFor(command => command.Requester!.BusinessName)
                .MustFit(0, TicketRequester.NameMaxLength)
                .When(command => Shows(command, SupportFormFields.BusinessName) && !Requires(command, SupportFormFields.BusinessName));
            RuleFor(command => command.Requester!.Address)
                .MustFit(0, TicketRequester.AddressMaxLength)
                .When(command => Shows(command, SupportFormFields.Address));
            RuleFor(command => command.Requester!.LandlinePhone)
                .MustFit(0, TicketRequester.LandlinePhoneMaxLength)
                .When(command => Shows(command, SupportFormFields.LandlinePhone));

            RuleFor(command => command.Requester!.Country)
                .Must(country => IsAlpha2(country) && countries.IsKnown(country!.Trim().ToUpperInvariant()))
                .WithMessage("The country must be a known ISO 3166 alpha-2 code.");
            RuleFor(command => command.Requester!.MobilePhone)
                .Must(text => !string.IsNullOrWhiteSpace(text))
                .WithMessage("The mobile phone is required.");
            // Plan F20: se guarda en E.164 con el país del solicitante; uno que no normaliza no es un celular de ese país.
            RuleFor(command => command.Requester!.MobilePhone)
                .Must((command, phone) => phones.ToE164(phone!.Trim(), CountryOf(command)!) is not null)
                .When(command => !string.IsNullOrWhiteSpace(command.Requester!.MobilePhone) && HasKnownCountry(command, countries))
                .WithMessage("The mobile phone is not valid for the requester's country.");

            // Plan F19: en Colombia la ciudad es de Geography; fuera, texto libre de 1 a 100 (Review del Bloque A, Minor 4: el dominio
            // no la acota y EF la cortaría con un 500).
            RuleFor(command => command.Requester!.CityId)
                .NotNull()
                .NotEqual(Guid.Empty)
                .When(command => HasKnownCountry(command, countries) && CountryOf(command) == Colombia);
            RuleFor(command => command.Requester!.CityName)
                .MustFit(1, TicketRequester.CityNameMaxLength)
                .When(command => HasKnownCountry(command, countries) && CountryOf(command) != Colombia);

            RuleFor(command => command.Requester!.Email).MustFit(1, TicketRequester.EmailMaxLength);
            RuleFor(command => command.Requester!.Email)
                .EmailAddress()
                .When(command => !string.IsNullOrWhiteSpace(command.Requester!.Email));
        });
    }

    /// <summary>Sin adjuntos, o hasta <see cref="SupportPublicAttachmentRules.MaxFiles"/> con <c>fileId</c> y <c>uploadKey</c> y sin
    /// repetir archivo.</summary>
    public static bool AttachmentsFit(IReadOnlyList<SupportPublicAttachmentRef>? attachments) =>
        attachments is null
        || (attachments.Count <= SupportPublicAttachmentRules.MaxFiles
            && attachments.All(item => item is { FileId: { } fileId, UploadKey: { } key } && fileId != Guid.Empty && key != Guid.Empty)
            && attachments.Select(item => item.FileId).Distinct().Count() == attachments.Count);

    /// <summary>Los campos que el formulario muestra para este cuerpo: la variante sale del documento que eligió la persona.</summary>
    internal static IReadOnlyList<SupportFormField> FieldsOf(FilePublicTicketCommand command)
    {
        var form = command.Form
            ?? throw new InvalidOperationException("FilePublicTicketCommand.Form must be set by the handler before validating.");
        var variant = SupportFormFields.VariantOf(command.Anonymous, form.ShowDocument, command.Requester?.DocumentType);
        return SupportFormFields.For(variant, form);
    }

    internal static bool Shows(FilePublicTicketCommand command, string key) => FieldsOf(command).Any(field => field.Key == key);

    private static bool Requires(FilePublicTicketCommand command, string key) =>
        FieldsOf(command).Any(field => field.Key == key && field.Required);

    internal static string? CountryOf(FilePublicTicketCommand command) => command.Requester?.Country?.Trim().ToUpperInvariant();

    private static bool HasKnownCountry(FilePublicTicketCommand command, ISupportCountries countries) =>
        IsAlpha2(command.Requester?.Country) && countries.IsKnown(CountryOf(command)!);

    private static bool IsAlpha2(string? country) =>
        country?.Trim() is { Length: 2 } trimmed && trimmed.All(char.IsAsciiLetter);
}

/// <summary>
/// Radicar desde el formulario público (spec §9.3 y §9.4, plan F17 y F21 a F23; spec 2026-11-15 §5.2 y §9, plan L14). Orden: resolver el
/// tenant → Turnstile → validador → versiones del formulario (y si permite adjuntos, spec §9.3) y de los documentos legales que aplican al
/// camino → tipo activo y publicado → foto del solicitante (con la ciudad de Geography) → adjuntos disponibles con su <c>uploadKey</c>
/// (plan F35) → provisión → cliente por documento → transacción (candado del cliente, radicado, ticket, clave, las dos aceptaciones
/// legales, adjuntos, auditoría del sistema) → confirmar → correo de apertura → su resultado en un segundo guardado de la clave. Cada
/// aceptación guarda el HTML tal como lo sirvió <c>GET /form</c> (<see cref="SupportServedLegal"/>): con datos, el aviso de privacidad y
/// los términos con la IP; anónimo, el aviso anónimo y los términos sin IP. Todo lo que puede responder 422 va antes
/// de la transacción, así un rechazo no gasta un radicado. El correo va después de confirmar: el radicado no depende de él (F23). Nada de
/// esto lee el contexto de ejecución ni la sesión, y el PIN no va a logs, auditoría ni traza: sólo su hash se guarda.
/// </summary>
public sealed class FilePublicTicketHandler(
    SupportPublicResolver resolver,
    ISupportCaptcha captcha,
    ISupportLegalDocumentRepository legalDocuments,
    ISupportHtmlSanitizer sanitizer,
    ISupportTicketTypeRepository types,
    ISupportGeography geography,
    ISupportPhoneNormalizer phones,
    ITenantModules tenantModules,
    ISupportCustomerLookup customers,
    ISupportProvisioner provisioner,
    ISupportTicketNumberGenerator numbers,
    ISupportTicketRepository tickets,
    ISupportTicketAccessRepository accesses,
    ISupportLegalAcceptanceRepository legalAcceptances,
    ISupportAttachmentStorage attachmentStorage,
    ISupportTicketAttachmentRepository attachments,
    ISupportPinHasher pinHasher,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    ISupportRealtime realtime,
    SupportTicketDtoComposer composer,
    SupportOpeningDelivery openingDelivery,
    ISupportPublicOptions publicOptions,
    IValidator<FilePublicTicketCommand> validator)
    : ICommandHandler<FilePublicTicketCommand, SupportPublicFiledDto>
{
    private static readonly string[] CreatedFields =
        ["ticketTypeId", "subject", "description", "intake", "replyChannel", "customerId", "requester", "isAnonymous", "legalAcceptances"];

    public async Task<SupportPublicFiledDto> HandleAsync(FilePublicTicketCommand command, CancellationToken cancellationToken)
    {
        var tenant = await resolver.ResolveAsync(command.TenantSlug, cancellationToken);
        if (!await captcha.VerifyAsync(command.TurnstileToken?.Trim() ?? string.Empty, command.VisitorIp, cancellationToken))
        {
            throw new SupportDomainException(SupportErrorCodes.PublicCaptchaInvalid, "The anti-bot challenge was not passed.");
        }

        var validated = command with { Form = tenant.Form };
        await validator.ValidateAndThrowAsync(validated, cancellationToken);

        if (validated.FormVersion != tenant.Form.Version)
        {
            throw new SupportDomainException(SupportErrorCodes.PublicFormOutdated, "The form changed after it was loaded.");
        }

        // Spec §9.3: con la versión con la que radica (ya igual a la vigente), si no permite adjuntos una lista no vacía es 422.
        if (validated.Attachments is { Count: > 0 })
        {
            CompletePublicAttachmentHandler.EnsureAllowed(tenant.Form);
        }

        var legal = await SupportServedLegal.ReadAsync(legalDocuments, sanitizer, tenant.TenantId, tenant.Name, cancellationToken);
        if (!IsCurrent(validated, legal))
        {
            throw new SupportDomainException(SupportErrorCodes.PublicLegalTextsOutdated, "The legal texts changed after they were loaded.");
        }

        var type = await types.FindAsync(tenant.TenantId, new SupportTicketTypeId(validated.TicketTypeId!.Value), cancellationToken);
        if (type is not { IsActive: true, IsPublished: true })
        {
            throw new SupportDomainException(
                SupportErrorCodes.TicketTypeUnavailable, "The ticket type does not exist or is not offered in the public form.");
        }

        var requester = validated.Anonymous ? null : await RequesterAsync(validated, cancellationToken);
        var files = await AvailableAttachmentsAsync(tenant.TenantId, validated.Attachments, cancellationToken);

        await provisioner.EnsureAsync(tenant.TenantId, cancellationToken);
        var customerId = await LinkedCustomerAsync(tenant.TenantId, requester, cancellationToken);

        var pin = SupportPin.Generate();
        SupportTicket ticket;
        SupportTicketAccess access;
        DateTimeOffset now;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            // El candado del cliente antes que la fila del radicado, como el alta manual (revisión final de la fase 1, m1).
            if (customerId is { } heldCustomerId)
            {
                await unitOfWork.HoldCustomerReferenceAsync(tenant.TenantId, heldCustomerId, cancellationToken);
            }

            var number = await numbers.NextAsync(tenant.TenantId, cancellationToken);
            var context = await composer.ReadContextAsync(tenant.TenantId, cancellationToken);
            now = context.Now;
            ticket = SupportTicket.CreateFromPublicForm(
                SupportTicketId.New(),
                tenant.TenantId,
                number,
                type,
                validated.Subject!,
                validated.Description!,
                requester,
                customerId,
                tenant.Form.Id,
                now,
                context.Clock);
            tickets.Add(ticket);
            access = SupportTicketAccess.Issue(ticket.Id, tenant.TenantId, pinHasher.Hash(pin), now);
            accesses.Add(access);
            // Spec 2026-11-15 §5.2 (plan L13 y L14): la prueba legal va en la misma transacción que el ticket, con el texto que la
            // persona vio. Un anónimo no deja IP en ninguna fila.
            foreach (var acceptance in Acceptances(legal, tenant.TenantId, ticket.Id, validated.Anonymous, command.VisitorIp, now))
            {
                legalAcceptances.Add(acceptance);
            }

            // Plan F35: en la misma transacción. Si otra radicación ya reclamó un archivo, IX_ticket_attachments_file lo rechaza al
            // guardar (SupportUnitOfWork lo traduce por nombre a support.public.attachment_invalid) y la transacción entera se
            // deshace, radicado incluido.
            foreach (var file in files)
            {
                attachments.Add(SupportTicketAttachment.Attach(
                    Guid.CreateVersion7(), tenant.TenantId, ticket.Id, file.FileId, file.Name, file.MimeType, file.SizeBytes, now));
            }

            auditPublisher.PublishSystem(
                tenant.TenantId,
                SupportAuditActions.TicketCreated,
                SupportAuditActions.Resources.Ticket,
                ticket.Id.ToString(),
                files.Count > 0 ? [.. CreatedFields, "attachments"] : CreatedFields,
                now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await realtime.TicketChangedAsync(tenant.TenantId, ticket.Id.Value, CancellationToken.None);
        var consultUrl = SupportOpeningSender.ConsultUrl(publicOptions, tenant.Slug, ticket.DisplayNumber);
        // Un anónimo no tiene a dónde: el PIN sólo se ve en esta respuesta. Con destino, la anotación es un segundo guardado, sólo de
        // la clave; si falla, el ticket ya está confirmado y el PIN se muestra igual (plan F23; revisión del Bloque B, Minor 2).
        var outcome = await openingDelivery.DeliverAsync(ticket, access, pin, consultUrl, now, cancellationToken);
        var emailed = outcome is { Channel: SupportOpeningSender.EmailChannel, Sent: true };

        return Filed(ticket, pin, tenant.Form.SuccessMessage, emailed, consultUrl);
    }

    // Plan L26: la fecha de radicación y el término salen de la foto del ticket, nunca del tipo vivo.
    private static SupportPublicFiledDto Filed(
        SupportTicket ticket, string? pin, string? successMessage, bool pinEmailed, string consultUrl) =>
        new(
            ticket.DisplayNumber,
            pin,
            ticket.ReceivedAt,
            ticket.DueOn,
            ticket.TermBusinessDays,
            ticket.TermIsLegal,
            successMessage,
            pinEmailed,
            consultUrl);

    // Plan L16: las versiones que aplican al camino tienen que ser las vigentes. La que no aplica ya la rechazó el validador.
    private static bool IsCurrent(FilePublicTicketCommand command, SupportServedLegal legal) =>
        command.TermsVersion == legal.Terms.Version.Version
        && (command.Anonymous
            ? command.AnonymousNoticeVersion == legal.AnonymousNotice.Version.Version
            : command.PrivacyNoticeVersion == legal.PrivacyNotice.Version.Version);

    // Spec 2026-11-15 §5.2: con datos, el aviso de privacidad (IP, pregunta mostrada y caracterización) y los términos con la IP;
    // anónimo, el aviso anónimo y los términos, los dos sin IP.
    private static IEnumerable<SupportLegalAcceptance> Acceptances(
        SupportServedLegal legal, Guid tenantId, SupportTicketId ticketId, bool anonymous, string visitorIp, DateTimeOffset now)
    {
        if (anonymous)
        {
            yield return SupportLegalAcceptance.Record(
                Guid.CreateVersion7(), tenantId, ticketId, legal.AnonymousNotice.Version, legal.AnonymousNotice.Html, null, null, null, now);
            yield return SupportLegalAcceptance.Record(
                Guid.CreateVersion7(), tenantId, ticketId, legal.Terms.Version, legal.Terms.Html, null, null, null, now);
            yield break;
        }

        yield return SupportLegalAcceptance.Record(
            Guid.CreateVersion7(),
            tenantId,
            ticketId,
            legal.PrivacyNotice.Version,
            legal.PrivacyNotice.Html,
            legal.AuthorizationQuestion,
            SupportLegalNotices.Characterization,
            visitorIp,
            now);
        yield return SupportLegalAcceptance.Record(
            Guid.CreateVersion7(), tenantId, ticketId, legal.Terms.Version, legal.Terms.Html, null, null, visitorIp, now);
    }

    // La foto del solicitante: sólo los campos que el formulario muestra, recortados, el celular en E.164 y la ciudad de Geography en
    // Colombia. Una ciudad que no existe es un 422 en su campo.
    private async Task<TicketRequester> RequesterAsync(FilePublicTicketCommand command, CancellationToken cancellationToken)
    {
        var body = command.Requester!;
        var country = FilePublicTicketValidator.CountryOf(command)!;
        bool Shows(string key) => FilePublicTicketValidator.Shows(command, key);

        Guid? cityId = null;
        string? cityName;
        if (country == "CO")
        {
            var city = await geography.FindCityAsync(body.CityId!.Value, cancellationToken)
                ?? throw new ValidationException([new ValidationFailure("Requester.CityId", "The city does not exist.")]);
            cityId = city.Id;
            cityName = city.Name;
        }
        else
        {
            cityName = Clean(body.CityName);
        }

        return new TicketRequester(
            Shows(SupportFormFields.FirstNames) ? Clean(body.FirstNames) : null,
            Shows(SupportFormFields.LastNames) ? Clean(body.LastNames) : null,
            Shows(SupportFormFields.BusinessName) ? Clean(body.BusinessName) : null,
            Shows(SupportFormFields.DocumentType) ? Clean(body.DocumentType)?.ToUpperInvariant() : null,
            Shows(SupportFormFields.DocumentNumber) ? Clean(body.DocumentNumber) : null,
            Clean(body.Email),
            phones.ToE164(body.MobilePhone!.Trim(), country),
            Shows(SupportFormFields.LandlinePhone) ? Clean(body.LandlinePhone) : null,
            country,
            Shows(SupportFormFields.Address) ? Clean(body.Address) : null,
            cityId,
            cityName);
    }

    // Plan F21: con customers prendido y documento en la foto, un solo cliente con ese documento se vincula; cero o varios, ninguno
    // (el detalle los ofrece como candidatos).
    private async Task<Guid?> LinkedCustomerAsync(Guid tenantId, TicketRequester? requester, CancellationToken cancellationToken)
    {
        if (requester is not { DocumentType: { } type, DocumentNumber: { } number }
            || await tenantModules.FindAsync(tenantId, cancellationToken) is not { } modules
            || !modules.IsEnabled(TenantModuleKeys.Customers))
        {
            return null;
        }

        var matches = await customers.FindIdsByDocumentAsync(tenantId, type, number, cancellationToken);
        return matches.Count == 1 ? matches[0] : null;
    }

    // Plan F32 y F35: cada archivo tiene que estar disponible, ser del tenant y haberse subido con ese uploadKey, que además tiene que
    // ser v4 (revisión final S2: la v8 de una nota nunca pasa por la de un visitante). Antes de la transacción, para que un rechazo no
    // gaste un radicado; uno que falla responde igual que uno que no existe.
    private async Task<IReadOnlyList<SupportAttachmentFile>> AvailableAttachmentsAsync(
        Guid tenantId, IReadOnlyList<SupportPublicAttachmentRef>? references, CancellationToken cancellationToken)
    {
        if (references is not { Count: > 0 })
        {
            return [];
        }

        var files = new List<SupportAttachmentFile>(references.Count);
        foreach (var reference in references)
        {
            if (!SupportPublicUploads.IsKey(reference.UploadKey!.Value))
            {
                throw CompletePublicAttachmentHandler.Invalid();
            }

            var file = await attachmentStorage.FindAvailableAsync(
                tenantId, reference.FileId!.Value, reference.UploadKey!.Value, cancellationToken);
            files.Add(file ?? throw CompletePublicAttachmentHandler.Invalid());
        }

        return files;
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
