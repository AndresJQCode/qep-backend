using System.Text.RegularExpressions;
using BuildingBlocks.Application;
using BuildingBlocks.Domain.Identification;
using FluentValidation;
using Modules.Support.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Support.Application;

/// <summary>La foto del solicitante tal como llega (plan D7). <c>CountryCode</c> vacío es el país del tenant.</summary>
public sealed record TicketRequesterRequest(
    string? FirstNames,
    string? LastNames,
    string? BusinessName,
    string? DocumentType,
    string? DocumentNumber,
    string? Email,
    string? MobilePhone,
    string? LandlinePhone,
    string? CountryCode,
    string? Address);

/// <summary>El cuerpo de <c>POST /tickets</c>, todo anulable para que su ausencia sea un <c>validation.failed</c> con el campo.</summary>
public sealed record CreateTicketRequest(
    Guid? TicketTypeId,
    string? Subject,
    string? Description,
    string? ManualMedium,
    string? ReplyChannel,
    Guid? CustomerId,
    TicketRequesterRequest? Requester,
    Guid? AssigneeMemberId);

public sealed record CreateTicketCommand(
    Guid TenantId,
    Guid TicketTypeId,
    string Subject,
    string Description,
    string ManualMedium,
    string ReplyChannel,
    Guid? CustomerId,
    TicketRequesterRequest? Requester,
    Guid? AssigneeMemberId)
    : ICommand<SupportTicketDetailDto>
{
    /// <summary>El país del tenant, con que se juzga el tipo de documento si el solicitante no trae el suyo. No viaja por el cable:
    /// lo pone el handler antes de validar.</summary>
    public string? TenantCountry { get; init; }

    public static CreateTicketCommand From(Guid tenantId, CreateTicketRequest request) => new(
        tenantId,
        request.TicketTypeId ?? Guid.Empty,
        request.Subject ?? string.Empty,
        request.Description ?? string.Empty,
        request.ManualMedium ?? string.Empty,
        request.ReplyChannel ?? string.Empty,
        request.CustomerId,
        request.Requester,
        request.AssigneeMemberId);
}

/// <summary>
/// Cubre cada guarda de <see cref="SupportTicket.CreateManual"/> (asunto y descripción) y cada columna de la foto del solicitante
/// (<see cref="TicketRequester"/>): ningún cuerpo que lo pase llega al dominio a lanzar ni revienta un INSERT. Mide los textos
/// recortados, como se guardan.
/// </summary>
public sealed class CreateTicketValidator : AbstractValidator<CreateTicketCommand>
{
    public CreateTicketValidator()
    {
        RuleFor(command => command.TicketTypeId).NotEqual(Guid.Empty);
        RuleFor(command => command.Subject).MustFit(1, SupportTicket.SubjectMaxLength);
        RuleFor(command => command.Description).MustFit(1, SupportTicket.DescriptionMaxLength);
        RuleFor(command => command.ManualMedium)
            .Must(text => SupportTicketRules.TryParseExact<ManualIntakeMedium>(text, out _))
            .WithMessage("ManualMedium must be Phone, InPerson, Email, Letter or Other.");
        RuleFor(command => command.ReplyChannel)
            .Must(text => SupportTicketRules.TryParseExact<SupportReplyChannel>(text, out _))
            .WithMessage("ReplyChannel must be Thread, WhatsAppTemplate or Email.");
        RuleFor(command => command.AssigneeMemberId).NotEqual(Guid.Empty);
        RuleFor(command => command.CustomerId).NotEqual(Guid.Empty);
        When(command => command.Requester is not null, () =>
        {
            RuleFor(command => command.Requester!.FirstNames).MustFit(0, TicketRequester.NameMaxLength);
            RuleFor(command => command.Requester!.LastNames).MustFit(0, TicketRequester.NameMaxLength);
            RuleFor(command => command.Requester!.BusinessName).MustFit(0, TicketRequester.NameMaxLength);
            // El par va junto: un número sin tipo no se puede validar, y un tipo sin número no identifica a nadie.
            RuleFor(command => command.Requester!.DocumentType)
                .Must(text => !string.IsNullOrWhiteSpace(text))
                .When(command => !string.IsNullOrWhiteSpace(command.Requester!.DocumentNumber))
                .WithMessage("The document type is required with a document number.");
            // Con un país mal escrito el error es del país, no del tipo.
            RuleFor(command => command.Requester!.DocumentType)
                .Must((command, type) => DocumentTypesByCountry.IsAllowed(type, CountryOf(command)))
                .When(command => !string.IsNullOrWhiteSpace(command.Requester!.DocumentType) && HasValidCountry(command))
                .WithMessage(command => $"The document type is not allowed for country '{CountryOf(command)}'.");
            RuleFor(command => command.Requester!.DocumentNumber)
                .Must(text => !string.IsNullOrWhiteSpace(text))
                .When(command => !string.IsNullOrWhiteSpace(command.Requester!.DocumentType))
                .WithMessage("The document number is required with a document type.");
            RuleFor(command => command.Requester!.DocumentNumber).MustFit(0, TicketRequester.DocumentNumberMaxLength);
            RuleFor(command => command.Requester!.Email).MustFit(0, TicketRequester.EmailMaxLength);
            RuleFor(command => command.Requester!.Email)
                .EmailAddress()
                .When(command => !string.IsNullOrWhiteSpace(command.Requester!.Email));
            // Recortado antes del patrón, como lo guarda el normalizador (revisión final, m4).
            RuleFor(command => command.Requester!.MobilePhone)
                .Must(text => TicketRequesterNormalizer.IsMobile(text!.Trim()))
                .When(command => !string.IsNullOrWhiteSpace(command.Requester!.MobilePhone))
                .WithMessage("The mobile phone must be E.164, like +573001234567.");
            RuleFor(command => command.Requester!.LandlinePhone).MustFit(0, TicketRequester.LandlinePhoneMaxLength);
            RuleFor(command => command.Requester!.CountryCode)
                .Must(IsAlpha2OrEmpty)
                .WithMessage("The country must be an ISO 3166 alpha-2 code.");
            RuleFor(command => command.Requester!.Address).MustFit(0, TicketRequester.AddressMaxLength);
        });
    }

    private static string CountryOf(CreateTicketCommand command) =>
        TicketRequesterNormalizer.CountryOf(
            command.Requester,
            command.TenantCountry
                ?? throw new InvalidOperationException("CreateTicketCommand.TenantCountry must be set by the handler before validating."));

    private static bool HasValidCountry(CreateTicketCommand command) => IsAlpha2OrEmpty(command.Requester?.CountryCode);

    // Vacío vale: es el país del tenant.
    private static bool IsAlpha2OrEmpty(string? country) =>
        country?.Trim() is not { Length: > 0 } trimmed || (trimmed.Length == 2 && trimmed.All(char.IsAsciiLetter));
}

/// <summary>El paso de la foto como llega a la que se guarda: textos recortados, vacío es <c>null</c>, tipo de documento y país en
/// mayúsculas, y sin país, el del tenant.</summary>
internal static class TicketRequesterNormalizer
{
    /// <summary>E.164: «+», un dígito de 1 a 9 y de 7 a 14 más (plan D7).</summary>
    public const string MobilePattern = @"^\+[1-9][0-9]{7,14}$";

    // La misma regla de FluentValidation que CreateTicketValidator aplica al correo del solicitante (EmailAddress), armada una vez:
    // el correo del cliente que se copia en la foto pasa por ella, sin copiar su lógica (revisión final, FU2/FU4).
    private static readonly InlineValidator<string> EmailFormat = CreateEmailFormat();

    /// <summary>Si <paramref name="text"/> (ya recortado) es un celular E.164.</summary>
    public static bool IsMobile(string text) => Regex.IsMatch(text, MobilePattern);

    /// <summary>Si <paramref name="text"/> (ya recortado) cumple la regla de formato del correo del solicitante.</summary>
    public static bool IsEmail(string text) => EmailFormat.Validate(text).IsValid;

    /// <summary>El país del solicitante o, sin él, el del tenant (<paramref name="tenantCountry"/>, que el alta lee de sus ajustes
    /// regionales: el mismo origen que el calendario de plazos).</summary>
    public static string CountryOf(TicketRequesterRequest? requester, string tenantCountry) =>
        (Clean(requester?.CountryCode) ?? tenantCountry.Trim()).ToUpperInvariant();

    /// <summary>Sin ningún dato, no hay solicitante.</summary>
    public static TicketRequester? Normalize(TicketRequesterRequest? requester, string tenantCountry)
    {
        if (requester is null)
        {
            return null;
        }

        var normalized = new TicketRequester(
            Clean(requester.FirstNames),
            Clean(requester.LastNames),
            Clean(requester.BusinessName),
            Clean(requester.DocumentType)?.ToUpperInvariant(),
            Clean(requester.DocumentNumber),
            Clean(requester.Email),
            Clean(requester.MobilePhone),
            Clean(requester.LandlinePhone),
            CountryOf(requester, tenantCountry),
            Clean(requester.Address));
        return normalized is
        {
            FirstNames: null, LastNames: null, BusinessName: null, DocumentType: null, DocumentNumber: null, Email: null,
            MobilePhone: null, LandlinePhone: null, Address: null,
        }
            ? null
            : normalized;
    }

    /// <summary>
    /// El contacto del cliente vinculado como respaldo (owner, 2026-11-14): su correo y su celular llenan sólo los campos que el
    /// solicitante dejó vacíos; lo que trae el solicitante siempre gana. Sin solicitante, la foto queda con sólo ese contacto y el
    /// país <paramref name="country"/> (el cliente vinculado ya cumple <c>requester_required</c>); sin contacto que copiar, no hay
    /// foto. Un valor del cliente que no cabe en la foto (correo de más de <see cref="TicketRequester.EmailMaxLength"/> o sin el formato
    /// que se le exige al del solicitante, celular que no es E.164) no se copia: recortarlo o guardarlo daría un contacto que no
    /// existe.
    /// </summary>
    public static TicketRequester? WithCustomerContact(TicketRequester? requester, SupportCustomerContact customer, string country)
    {
        var email = Clean(customer.Email) is { Length: <= TicketRequester.EmailMaxLength } fittingEmail && IsEmail(fittingEmail)
            ? fittingEmail
            : null;
        var mobile = Clean(customer.MobilePhone) is { } phone && IsMobile(phone) ? phone : null;
        if (requester is null)
        {
            return email is null && mobile is null
                ? null
                : new TicketRequester(null, null, null, null, null, email, mobile, null, country, null);
        }

        return requester with { Email = requester.Email ?? email, MobilePhone = requester.MobilePhone ?? mobile };
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static InlineValidator<string> CreateEmailFormat()
    {
        var validator = new InlineValidator<string>();
        validator.RuleFor(text => text).EmailAddress().OverridePropertyName("Email");
        return validator;
    }
}

internal static class SupportTextRules
{
    /// <summary>Entre <paramref name="min"/> y <paramref name="max"/> caracteres, medido recortado como lo guarda el dominio.</summary>
    public static IRuleBuilderOptions<T, string?> MustFit<T>(this IRuleBuilder<T, string?> rule, int min, int max) =>
        rule.Must(text => (text?.Trim().Length ?? 0) is var length && length >= min && length <= max)
            .WithMessage(min > 0
                ? $"The text must have between {min} and {max} characters."
                : $"The text cannot exceed {max} characters.");
}

/// <summary>
/// El alta manual (spec §8.3, plan D6–D8 y D14). Orden: autorización → membresía de quien actúa → validador → solicitante →
/// provisión → tipo activo → cliente (y su contacto de respaldo en la foto) → responsable → canal → transacción → candado del
/// cliente → radicado → contexto (reloj leído una vez) → ticket → auditoría → guardar → confirmar.
/// Todo lo que puede responder 422 va antes de la transacción, así un rechazo no gasta un radicado. La provisión abre su propia
/// transacción, así que va antes de <see cref="ISupportUnitOfWork.BeginTransactionAsync"/>.
/// </summary>
public sealed class CreateTicketHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantDirectory tenantDirectory,
    IMembershipDirectory membershipDirectory,
    ISupportProvisioner provisioner,
    ISupportTicketTypeRepository types,
    ISupportTicketRepository tickets,
    ISupportCustomerLookup customers,
    ISupportMemberDirectory members,
    ISupportTicketNumberGenerator numbers,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    SupportTicketDtoComposer composer,
    IValidator<CreateTicketCommand> validator)
    : ICommandHandler<CreateTicketCommand, SupportTicketDetailDto>
{
    private static readonly string[] CreatedFields =
        ["ticketTypeId", "subject", "description", "manualMedium", "replyChannel", "customerId", "requester", "assigneeMemberId"];

    public async Task<SupportTicketDetailDto> HandleAsync(CreateTicketCommand command, CancellationToken cancellationToken)
    {
        await SupportAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, SupportPermissions.Manage, cancellationToken);
        var actor = await membershipDirectory.FindActiveMembershipIdAsync(executionContext.SubjectId, command.TenantId, cancellationToken)
            ?? throw SupportAuthorization.Denied();
        // El mismo origen y la misma regla que SupportBusinessCalendarSource (revisión final, B6/m6): sin ajustes regionales, lanza.
        // No pasa: quien llega acá tiene membresía activa, y una membresía es de un tenant con su fila.
        var tenantCountry = (await tenantDirectory.GetRegionalSettingsAsync(new TenantId(command.TenantId), cancellationToken)
            ?? throw new InvalidOperationException($"Tenant '{command.TenantId}' has no regional settings.")).Country;
        var validated = command with { TenantCountry = tenantCountry };
        await validator.ValidateAndThrowAsync(validated, cancellationToken);
        if (!SupportTicketRules.TryParseExact<ManualIntakeMedium>(validated.ManualMedium, out var medium) ||
            !SupportTicketRules.TryParseExact<SupportReplyChannel>(validated.ReplyChannel, out var channel))
        {
            throw new InvalidOperationException("ManualMedium or ReplyChannel passed validation but is not a known value.");
        }

        var requester = TicketRequesterNormalizer.Normalize(validated.Requester, tenantCountry);
        SupportTicketRules.EnsureRequester(validated.CustomerId, requester);

        await provisioner.EnsureAsync(validated.TenantId, cancellationToken);
        var type = await SupportTicketRules.EnsureTypeAsync(types, validated.TenantId, validated.TicketTypeId, cancellationToken);
        if (validated.CustomerId is { } customerId)
        {
            var customer = await SupportTicketRules.EnsureCustomerAsync(
                tenantModules, customers, validated.TenantId, customerId, cancellationToken);
            requester = TicketRequesterNormalizer.WithCustomerContact(
                requester, customer, TicketRequesterNormalizer.CountryOf(validated.Requester, tenantCountry));
        }

        await SupportTicketRules.EnsureAssigneeAsync(members, validated.TenantId, validated.AssigneeMemberId, cancellationToken);
        SupportTicketRules.EnsureReplyChannel(channel, requester);

        SupportReadContext context;
        SupportTicket ticket;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            // Orden de candados (revisión final, m1): el del cliente antes que la fila del radicado, que es de todo el tenant. Así un
            // alta que espera a que se borre su cliente no tiene la fila y no frena las demás altas.
            if (validated.CustomerId is { } heldCustomerId)
            {
                await unitOfWork.HoldCustomerReferenceAsync(validated.TenantId, heldCustomerId, cancellationToken);
            }

            var number = await numbers.NextAsync(validated.TenantId, cancellationToken);
            // El reloj se lee ya con el radicado (fix round 1): con la fila del radicado tomada las altas van en fila, así la hora de
            // radicación sigue el orden del número aunque una haya esperado el candado de su cliente. Es sólo lectura: no toma candados.
            context = await composer.ReadContextAsync(validated.TenantId, cancellationToken);
            var now = context.Now;
            ticket = SupportTicket.CreateManual(
                SupportTicketId.New(),
                validated.TenantId,
                number,
                type,
                validated.Subject,
                validated.Description,
                medium,
                channel,
                validated.CustomerId,
                requester,
                validated.AssigneeMemberId,
                actor,
                now,
                context.Clock);
            tickets.Add(ticket);
            auditPublisher.Publish(
                validated.TenantId,
                executionContext.SubjectId,
                SupportAuditActions.TicketCreated,
                SupportAuditActions.Resources.Ticket,
                ticket.Id.ToString(),
                "success",
                CreatedFields,
                now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return await composer.ComposeDetailAsync(ticket, context, cancellationToken);
    }
}
