using BuildingBlocks.Application;
using BuildingBlocks.Application.RichText;
using FluentValidation;
using FluentValidation.Results;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>Las secciones configurables del PDF de un tenant (spec 2026-11-16 §5.1), en <c>quotations.pdf_sections</c>.</summary>
public interface IPdfContentStore
{
    /// <summary>Una sección sin fila sale como <see cref="PdfSection.Empty"/>; un tenant sin filas, <see cref="PdfContent.Empty"/>. Leer
    /// no escribe nada.</summary>
    Task<PdfContent> GetAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Upsert de las tres filas, dentro de la transacción del handler (<c>IQuotationsUnitOfWork.BeginTransactionAsync</c>):
    /// así el contenido y la auditoría del outbox confirman juntos.</summary>
    Task SaveAsync(Guid tenantId, PdfContent content, CancellationToken cancellationToken);

    /// <summary>Vence los PDF de cotización guardados del tenant (huella en null), dentro de la misma transacción del handler. La
    /// huella no incluye la cuenta para el pago (spec 2026-11-16 pdf-payment-account, decisión 5), así que sin esto un PDF cacheado
    /// seguiría mostrándola, o no, después de cambiarla.</summary>
    Task InvalidateQuotationPdfsAsync(Guid tenantId, CancellationToken cancellationToken);
}

public sealed record PdfSectionDto(bool Enabled, bool InQuotation, bool InOrder, string Title, string ContentHtml);

/// <summary>La cuenta para el pago: sólo los flags, con la misma regla de una sección (spec 2026-11-16 pdf-payment-account).</summary>
public sealed record PdfPaymentAccountDto(bool Enabled, bool InQuotation, bool InOrder);

/// <summary>El documento entero de la pantalla. <see cref="Variables"/> va último, como en los documentos legales de Soporte: la
/// consola arma el menú de variables con esa lista y no la conoce por su cuenta (spec §4.2).</summary>
public sealed record PdfContentDto(
    PdfSectionDto PaymentNotice,
    PdfSectionDto Terms,
    PdfSectionDto DeliveryProcess,
    PdfPaymentAccountDto PaymentAccount,
    IReadOnlyList<QuotationPdfVariable> Variables);

public sealed record GetPdfContentQuery(Guid TenantId) : IQuery<PdfContentDto>;

/// <summary>Secciones y cuenta nullable a propósito: un cuerpo al que le falta una llega al validador como null y es un 422, en vez
/// de borrarla o apagarla en silencio.</summary>
public sealed record UpdatePdfContentCommand(
    Guid TenantId,
    PdfSectionDto? PaymentNotice,
    PdfSectionDto? Terms,
    PdfSectionDto? DeliveryProcess,
    PdfPaymentAccountDto? PaymentAccount) : ICommand<PdfContentDto>;

public sealed class GetPdfContentHandler(
    IPdfContentStore store,
    ITenantModules tenantModules,
    IExecutionContext executionContext)
    : IQueryHandler<GetPdfContentQuery, PdfContentDto>
{
    public async Task<PdfContentDto> HandleAsync(GetPdfContentQuery query, CancellationToken cancellationToken)
    {
        // Los permisos de settings, como el mínimo de compra (QuotationSettingsEndpoints).
        QuotationsAuthorization.EnsureAuthorized(executionContext, query.TenantId, TenancyPermissions.SettingsRead);
        await TenantModuleGuard.EnsureAnyEnabledAsync(
            tenantModules, query.TenantId, QuotationsModuleSets.QuotationsOrOrders, cancellationToken);

        return PdfContentMapping.ToDto(await store.GetAsync(query.TenantId, cancellationToken));
    }
}

public sealed class UpdatePdfContentValidator : AbstractValidator<UpdatePdfContentCommand>
{
    public UpdatePdfContentValidator()
    {
        // Cada sección con su propia clave (PaymentNotice.Title, Terms.ContentHtml…): es lo que el formulario sabe marcar.
        RuleFor(command => command.PaymentNotice).NotNull();
        RuleFor(command => command.PaymentNotice!).SetValidator(new PdfSectionDtoValidator())
            .When(command => command.PaymentNotice is not null);
        RuleFor(command => command.Terms).NotNull();
        RuleFor(command => command.Terms!).SetValidator(new PdfSectionDtoValidator())
            .When(command => command.Terms is not null);
        RuleFor(command => command.DeliveryProcess).NotNull();
        RuleFor(command => command.DeliveryProcess!).SetValidator(new PdfSectionDtoValidator())
            .When(command => command.DeliveryProcess is not null);
        // Un cliente que no la conoce (una consola vieja) no la apaga sin saberlo: el cuerpo sin cuenta es un 422.
        RuleFor(command => command.PaymentAccount).NotNull();
    }

    private sealed class PdfSectionDtoValidator : AbstractValidator<PdfSectionDto>
    {
        public PdfSectionDtoValidator()
        {
            RuleFor(section => section.Title).NotNull().MaximumLength(PdfContentRules.MaxTitleLength);
            // El largo del HTML se mide en el handler, después de sanear (spec §4.1): el crudo puede traer lo que el saneador quita.
            RuleFor(section => section.ContentHtml).NotNull();
        }
    }
}

/// <summary>Reemplazo del documento entero, sin <c>If-Match</c>: último que guarda gana, como <c>PUT …/quotations/settings</c>
/// (spec §4.2). Mismo orden que <see cref="UpdateQuotationSettingsHandler"/>.</summary>
public sealed class UpdatePdfContentHandler(
    IPdfContentStore store,
    IRichTextSanitizer sanitizer,
    ITenantModules tenantModules,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<UpdatePdfContentCommand> validator)
    : ICommandHandler<UpdatePdfContentCommand, PdfContentDto>
{
    public const string AuditAction = "quotations.pdf_content.updated";

    public async Task<PdfContentDto> HandleAsync(UpdatePdfContentCommand command, CancellationToken cancellationToken)
    {
        // Autorizar antes de validar: alguien de otro tenant no recibe el mapa de campos.
        QuotationsAuthorization.EnsureAuthorized(executionContext, command.TenantId, TenancyPermissions.SettingsUpdate);
        await TenantModuleGuard.EnsureAnyEnabledAsync(
            tenantModules, command.TenantId, QuotationsModuleSets.QuotationsOrOrders, cancellationToken);
        // Las fallas del validador y las del saneador van juntas en un solo 422: si el saneador rechaza una sección, el formulario
        // igual tiene que marcar el título demasiado largo de otra.
        var failures = (await validator.ValidateAsync(command, cancellationToken)).Errors.ToList();
        var paymentNotice = Sanitize(PdfSectionKey.PaymentNotice, command.PaymentNotice, failures);
        var terms = Sanitize(PdfSectionKey.Terms, command.Terms, failures);
        var deliveryProcess = Sanitize(PdfSectionKey.DeliveryProcess, command.DeliveryProcess, failures);
        if (failures.Count > 0)
        {
            throw new ValidationException(failures);
        }

        // Las variables se miran sobre lo saneado: es lo que se guarda y lo que el PDF resuelve.
        var content = PdfContent.Create(paymentNotice!, terms!, deliveryProcess!, ToDomain(command.PaymentAccount!));

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var previous = await store.GetAsync(command.TenantId, cancellationToken);
        await store.SaveAsync(command.TenantId, content, cancellationToken);
        // Sólo la cotización tiene PDF cacheado: un cambio que sólo toca el pedido, o que no cambia nada, no vence ninguno.
        if (previous.PaymentAccount.PrintsOn(isOrder: false) != content.PaymentAccount.PrintsOn(isOrder: false))
        {
            await store.InvalidateQuotationPdfsAsync(command.TenantId, cancellationToken);
        }

        // Sólo los nombres de los campos que cambiaron, nunca el texto (spec §4.2): el contenido lo escribe el tenant y puede traer
        // cualquier cosa. Un PUT que no cambia nada se guarda y responde igual, pero no deja una entrada de auditoría vacía.
        var changedFields = PdfContentMapping.ChangedFields(previous, content).ToList();
        if (changedFields.Count > 0)
        {
            auditPublisher.PublishWithDetails(
                command.TenantId,
                executionContext.SubjectId,
                AuditAction,
                command.TenantId.ToString(),
                "success",
                clock.UtcNow,
                changedFields.ToDictionary(path => path, _ => (string?)null, StringComparer.Ordinal));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return PdfContentMapping.ToDto(content);
    }

    private static PdfPaymentAccount ToDomain(PdfPaymentAccountDto account) =>
        new(account.Enabled, account.InQuotation, account.InOrder);

    // Sanea el HTML con la clave de la sección (Terms.ContentHtml) y suma las fallas que sólo se pueden medir después de sanear: el
    // largo y que una sección prendida tenga texto que se vea. Una sección que falta o sin HTML ya la marcó el validador: devuelve
    // null, y también cuando el saneador la rechaza (su falla se suma a las demás en vez de cortar el resto).
    private PdfSection? Sanitize(PdfSectionKey key, PdfSectionDto? section, List<ValidationFailure> failures)
    {
        if (section?.ContentHtml is null)
        {
            return null;
        }

        var propertyName = $"{key}.{nameof(PdfSectionDto.ContentHtml)}";
        RichTextSanitized sanitized;
        try
        {
            sanitized = sanitizer.Sanitize(section.ContentHtml, propertyName);
        }
        catch (ValidationException rejected)
        {
            failures.AddRange(rejected.Errors);
            return null;
        }

        if (sanitized.Html.Length > PdfContentRules.MaxContentHtmlLength)
        {
            failures.Add(new ValidationFailure(
                propertyName, $"The content cannot exceed {PdfContentRules.MaxContentHtmlLength} characters after sanitizing."));
        }
        else if (section.Enabled && !sanitized.HasVisibleText)
        {
            failures.Add(new ValidationFailure(propertyName, "An enabled section needs visible text."));
        }

        return new PdfSection(key, section.Enabled, section.InQuotation, section.InOrder, section.Title, sanitized.Html);
    }
}

/// <summary>Los límites de una sección (spec §4.1). <see cref="MaxTitleLength"/> es también el <c>varchar</c> de
/// <c>pdf_sections.title</c> (<c>QuotationsDbContext</c> lo lee de acá): cambiarlo exige una migración.</summary>
public static class PdfContentRules
{
    public const int MaxTitleLength = 120;

    public const int MaxContentHtmlLength = 20_000;
}

internal static class PdfContentMapping
{
    public static PdfContentDto ToDto(PdfContent content) => new(
        ToDto(content.PaymentNotice),
        ToDto(content.Terms),
        ToDto(content.DeliveryProcess),
        new PdfPaymentAccountDto(content.PaymentAccount.Enabled, content.PaymentAccount.InQuotation, content.PaymentAccount.InOrder),
        QuotationPdfVariables.All);

    // Las rutas con la misma forma que las claves de errors (Terms.ContentHtml), en el orden de las secciones.
    public static IEnumerable<string> ChangedFields(PdfContent previous, PdfContent current)
    {
        foreach (var (before, after) in previous.All.Zip(current.All))
        {
            var prefix = after.Key.ToString();
            if (before.Enabled != after.Enabled)
            {
                yield return $"{prefix}.{nameof(PdfSection.Enabled)}";
            }

            if (before.InQuotation != after.InQuotation)
            {
                yield return $"{prefix}.{nameof(PdfSection.InQuotation)}";
            }

            if (before.InOrder != after.InOrder)
            {
                yield return $"{prefix}.{nameof(PdfSection.InOrder)}";
            }

            if (!string.Equals(before.Title, after.Title, StringComparison.Ordinal))
            {
                yield return $"{prefix}.{nameof(PdfSection.Title)}";
            }

            if (!string.Equals(before.ContentHtml, after.ContentHtml, StringComparison.Ordinal))
            {
                yield return $"{prefix}.{nameof(PdfSection.ContentHtml)}";
            }
        }

        // La cuenta va después de las secciones, con la misma forma de clave que el campo del 422 (PaymentAccount.Enabled).
        const string account = nameof(PdfContent.PaymentAccount);
        if (previous.PaymentAccount.Enabled != current.PaymentAccount.Enabled)
        {
            yield return $"{account}.{nameof(PdfPaymentAccount.Enabled)}";
        }

        if (previous.PaymentAccount.InQuotation != current.PaymentAccount.InQuotation)
        {
            yield return $"{account}.{nameof(PdfPaymentAccount.InQuotation)}";
        }

        if (previous.PaymentAccount.InOrder != current.PaymentAccount.InOrder)
        {
            yield return $"{account}.{nameof(PdfPaymentAccount.InOrder)}";
        }
    }

    private static PdfSectionDto ToDto(PdfSection section) =>
        new(section.Enabled, section.InQuotation, section.InOrder, section.Title, section.ContentHtml);
}
