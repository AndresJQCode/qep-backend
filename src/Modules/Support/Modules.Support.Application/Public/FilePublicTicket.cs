using BuildingBlocks.Application;
using BuildingBlocks.Domain.Identification;
using FluentValidation;
using FluentValidation.Results;
using Modules.Support.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Support.Application;

/// <param name="VisitorIp">La IP del visitante (<c>RemoteIpAddress</c>, ya corregida por <c>X-Real-IP</c>): para Turnstile y para el
/// consentimiento. Nunca para un anónimo.</param>
public sealed record FilePublicTicketCommand(
    string? TenantSlug,
    string VisitorIp,
    Guid? TicketTypeId,
    string? Subject,
    string? Description,
    bool Anonymous,
    SupportPublicRequesterRequest? Requester,
    bool AcceptsDataPolicy,
    int? LegalTextVersion,
    int? FormVersion,
    string? TurnstileToken)
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
            request.LegalTextVersion,
            request.FormVersion,
            request.TurnstileToken);
    }
}

/// <summary>
/// El radicado del formulario público (plan F18 a F20). Qué campos se piden y cuáles son obligatorios lo dice
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

        When(command => command.Anonymous, () =>
            RuleFor(command => command.Requester)
                .Null()
                .WithMessage("An anonymous ticket cannot carry requester data."));

        When(command => !command.Anonymous, () =>
        {
            RuleFor(command => command.Requester).NotNull();
            RuleFor(command => command.LegalTextVersion).NotNull();
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
/// Radicar desde el formulario público (spec §9.3 y §9.4, plan F17 y F21 a F23). Orden: resolver el tenant → Turnstile → validador →
/// versiones del formulario y de los textos legales → tipo activo y publicado → foto del solicitante (con la ciudad de Geography) →
/// provisión → cliente por documento → transacción (candado del cliente, radicado, ticket, clave, consentimiento, auditoría del
/// sistema) → confirmar → correo de apertura → su resultado en un segundo guardado de la clave. Todo lo que puede responder 422 va antes
/// de la transacción, así un rechazo no gasta un radicado. El correo va después de confirmar: el radicado no depende de él (F23). Nada de
/// esto lee el contexto de ejecución ni la sesión, y el PIN no va a logs, auditoría ni traza: sólo su hash se guarda.
/// </summary>
public sealed class FilePublicTicketHandler(
    SupportPublicResolver resolver,
    ISupportCaptcha captcha,
    ISupportLegalTextRepository legalTexts,
    ISupportTicketTypeRepository types,
    ISupportGeography geography,
    ISupportPhoneNormalizer phones,
    ITenantModules tenantModules,
    ISupportCustomerLookup customers,
    ISupportProvisioner provisioner,
    ISupportTicketNumberGenerator numbers,
    ISupportTicketRepository tickets,
    ISupportTicketAccessRepository accesses,
    ISupportConsentRepository consents,
    ISupportPinHasher pinHasher,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    SupportTicketDtoComposer composer,
    ISupportOpeningEmail openingEmail,
    ISupportPublicOptions publicOptions,
    IValidator<FilePublicTicketCommand> validator)
    : ICommandHandler<FilePublicTicketCommand, SupportPublicFiledDto>
{
    /// <summary>El canal registrado en <c>ticket_access.last_sent_channel</c> cuando la información sale por correo.</summary>
    public const string EmailChannel = "Email";

    private static readonly string[] CreatedFields =
        ["ticketTypeId", "subject", "description", "intake", "replyChannel", "customerId", "requester", "isAnonymous"];

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

        var legal = await legalTexts.CurrentAsync(tenant.TenantId, cancellationToken) ?? throw SupportPublicResolver.NotFound();
        if (!validated.Anonymous && validated.LegalTextVersion != legal.Version)
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
            // Plan F17: la prueba legal va en la misma transacción que el ticket, con el texto que la persona vio. Un anónimo no deja
            // consentimiento ni IP.
            if (requester is not null)
            {
                consents.Add(SupportConsent.Record(Guid.CreateVersion7(), tenant.TenantId, ticket.Id, legal, command.VisitorIp, now));
            }

            auditPublisher.PublishSystem(
                tenant.TenantId,
                SupportAuditActions.TicketCreated,
                SupportAuditActions.Resources.Ticket,
                ticket.Id.ToString(),
                CreatedFields,
                now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        var consultUrl = ConsultUrl(publicOptions, tenant.Slug, ticket.DisplayNumber);
        var emailed = false;
        if (requester is { Email: { } email })
        {
            var result = await openingEmail.SendAsync(
                new SupportOpeningEmail(email, tenant.Name, ticket.DisplayNumber, ticket.TypeName, ticket.DueOn, consultUrl, pin),
                cancellationToken);
            emailed = result is SupportChannelSendResult.Sent;
            // Un segundo guardado, sólo de la clave: no toca el ticket ni su versión.
            access.RecordDelivery(EmailChannel, FailureCodeOf(result), now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return new SupportPublicFiledDto(ticket.DisplayNumber, pin, ticket.DueOn, tenant.Form.SuccessMessage, emailed, consultUrl);
    }

    /// <summary>Plan F29: el enlace de consulta. Lleva el radicado, nunca el PIN.</summary>
    internal static string ConsultUrl(ISupportPublicOptions options, string slug, string displayNumber) =>
        $"{options.BaseUrl}/{slug}/consultar?radicado={Uri.EscapeDataString(displayNumber)}";

    /// <summary>El código que queda en <c>ticket_access.last_failure_code</c>: los de <see cref="SupportChannelReasons"/>, nunca uno
    /// nuevo.</summary>
    internal static string? FailureCodeOf(SupportChannelSendResult result) => result switch
    {
        SupportChannelSendResult.Sent => null,
        SupportChannelSendResult.Rejected rejected => rejected.Reason,
        SupportChannelSendResult.Failed failed => failed.Code,
        _ => SupportChannelReasons.SendFailed,
    };

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

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
