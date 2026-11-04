using BuildingBlocks.Application;
using FluentValidation;
using Modules.Store.Domain;

namespace Modules.Store.Application;

/// <param name="CityName">Sólo fuera de Colombia; con <c>CO</c> van los ids de DIVIPOLA.</param>
public sealed record PublicBillingInput(
    string Name,
    string IdentificationType,
    string IdentificationNumber,
    string Phone,
    string Email,
    string Address,
    string Country,
    Guid? DepartmentId,
    Guid? CityId,
    string? CityName);

public sealed record PublicShippingInput(
    string? RecipientName,
    string Phone,
    string Address,
    string Country,
    Guid? DepartmentId,
    Guid? CityId,
    string? CityName);

/// <summary>El cuerpo de <c>POST /order-requests</c>. La moneda no se valida acá: una moneda ausente es un código de la tienda y no <c>validation.failed</c>.</summary>
public sealed record PublicOrderRequestBody(
    Guid ClientRequestId,
    string TurnstileToken,
    string? Currency,
    PublicBillingInput Billing,
    PublicShippingInput? Shipping,
    string? Note,
    bool AcceptsDataPolicy,
    IReadOnlyList<PublicCartLineRequest> Lines);

/// <param name="Number">El consecutivo con formato de pantalla: <c>T-0001</c>.</param>
public sealed record PublicOrderRequestReceiptDto(Guid Id, string Number, decimal Total, string Currency);

/// <param name="VisitorIp">La IP que declaró el storefront con su clave; es la del consentimiento, nunca la del par directo.</param>
public sealed record SubmitOrderRequestCommand(string Host, string VisitorIp, PublicOrderRequestBody Body)
    : ICommand<(PublicOrderRequestReceiptDto Receipt, bool Created)>;

/// <summary>
/// La forma del cuerpo, con los límites de la tienda pública. Los nombres de propiedad son los de FluentValidation
/// (<c>Billing.Email</c>), que es la clave del mapa <c>errors</c>. Los desajustes de carril de ciudad (ids para otro país, nombre para
/// Colombia) no se juzgan acá: son <c>city_invalid</c> en el manejador. Acá sólo se rechaza un bloque sin ciudad alguna o con una pareja
/// de ids a medias.
/// </summary>
public sealed class PublicOrderRequestBodyValidator : AbstractValidator<PublicOrderRequestBody>
{
    public const int TokenMaxLength = 2048;

    public const int PhoneMaxLength = 32;

    public const int AddressMaxLength = 200;

    private static readonly string[] IdentificationTypes = ["NIT", "CC", "CE", "PASAPORTE"];

    public PublicOrderRequestBodyValidator()
    {
        RuleFor(body => body.ClientRequestId).NotEqual(Guid.Empty);
        RuleFor(body => body.TurnstileToken).NotEmpty().MaximumLength(TokenMaxLength);
        RuleFor(body => body.Note).MaximumLength(StoreOrderRequest.NoteMaxLength);

        RuleFor(body => body.Billing).NotNull();
        RuleFor(body => body.Billing).SetValidator(new BillingValidator()).When(body => body.Billing is not null);
        RuleFor(body => body.Shipping!).SetValidator(new ShippingValidator()).When(body => body.Shipping is not null);

        RuleFor(body => body.Lines)
            .NotNull()
            .Must(lines => lines is null || (lines.Count >= 1 && lines.Count <= QuotePublicCartValidator.MaxLines))
            .WithMessage($"El carrito lleva de 1 a {QuotePublicCartValidator.MaxLines} líneas.");
        RuleForEach(body => body.Lines)
            .Must(line => line is { VariantId: var id } && id != Guid.Empty)
            .WithMessage("Cada línea necesita su variante.")
            .Must(line => line is { Quantity: >= 1 and <= QuotePublicCartValidator.MaxQuantity })
            .WithMessage($"La cantidad va de 1 a {QuotePublicCartValidator.MaxQuantity}.");
    }

    private static bool CountryShape(string? code) => code is null || StoreCountry.IsShapeValid(code.Trim());

    private static bool HasCity(Guid? departmentId, Guid? cityId, string? cityName) =>
        departmentId.HasValue || cityId.HasValue || !string.IsNullOrWhiteSpace(cityName);

    // Con CO los dos ids van juntos y no vacíos; el resto (nombre para CO, ids para otro país) es city_invalid del manejador.
    private static bool ColombianIdsComplete(string? country, Guid? departmentId, Guid? cityId) =>
        !StoreCountry.IsColombia(country?.Trim() ?? string.Empty)
        || (departmentId.HasValue == cityId.HasValue && departmentId != Guid.Empty && cityId != Guid.Empty);

    private sealed class BillingValidator : AbstractValidator<PublicBillingInput>
    {
        public BillingValidator()
        {
            RuleFor(b => b.Name).NotEmpty().MaximumLength(150);
            RuleFor(b => b.IdentificationType)
                .NotEmpty()
                .Must(type => type is null || IdentificationTypes.Contains(type.Trim(), StringComparer.OrdinalIgnoreCase))
                .WithMessage("El tipo de documento no es válido.");
            RuleFor(b => b.IdentificationNumber).NotEmpty().MaximumLength(32);
            RuleFor(b => b.Phone).NotEmpty().MaximumLength(PhoneMaxLength);
            RuleFor(b => b.Email).NotEmpty().MaximumLength(254).EmailAddress();
            RuleFor(b => b.Address).NotEmpty().MaximumLength(AddressMaxLength);
            RuleFor(b => b.Country).NotEmpty().Must(CountryShape).WithMessage("El país debe ser un código de 2 letras.");
            RuleFor(b => b.CityName).MaximumLength(StorePlace.NameMaxLength);
            RuleFor(b => b.CityName)
                .Must((b, name) => HasCity(b.DepartmentId, b.CityId, name))
                .WithMessage("La ciudad es obligatoria.");
            RuleFor(b => b.CityId)
                .Must((b, id) => ColombianIdsComplete(b.Country, b.DepartmentId, id))
                .WithMessage("En Colombia el departamento y la ciudad van juntos.");
        }
    }

    private sealed class ShippingValidator : AbstractValidator<PublicShippingInput>
    {
        public ShippingValidator()
        {
            RuleFor(s => s.RecipientName).MaximumLength(120);
            RuleFor(s => s.Phone).NotEmpty().MaximumLength(PhoneMaxLength);
            RuleFor(s => s.Address).NotEmpty().MaximumLength(AddressMaxLength);
            RuleFor(s => s.Country).NotEmpty().Must(CountryShape).WithMessage("El país debe ser un código de 2 letras.");
            RuleFor(s => s.CityName).MaximumLength(StorePlace.NameMaxLength);
            RuleFor(s => s.CityName)
                .Must((s, name) => HasCity(s.DepartmentId, s.CityId, name))
                .WithMessage("La ciudad es obligatoria.");
            RuleFor(s => s.CityId)
                .Must((s, id) => ColombianIdsComplete(s.Country, s.DepartmentId, id))
                .WithMessage("En Colombia el departamento y la ciudad van juntos.");
        }
    }
}

public sealed class SubmitOrderRequestHandler(
    StorefrontResolver resolver,
    IStoreOrderRequestRepository requests,
    IStoreRequestNumberGenerator numbers,
    IStoreGeography geography,
    IStorePhoneNormalizer phones,
    ICaptchaVerifier captcha,
    PublicCartPricing pricing,
    IStoreUnitOfWork unitOfWork,
    IStoreAuditPublisher auditPublisher,
    IClock clock,
    StoreOptions options,
    IValidator<PublicOrderRequestBody> validator)
    : ICommandHandler<SubmitOrderRequestCommand, (PublicOrderRequestReceiptDto Receipt, bool Created)>
{
    private static readonly TimeSpan CapWindow = TimeSpan.FromHours(24);

    public async Task<(PublicOrderRequestReceiptDto Receipt, bool Created)> HandleAsync(
        SubmitOrderRequestCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var body = command.Body;

        // El orden evita que un rechazo escriba algo o gaste un número: lo barato y local primero, Cloudflare sólo con el formulario
        // válido, y todo antes de abrir la transacción.
        var (store, _) = await resolver.ResolveAsync(command.Host, cancellationToken);
        await validator.ValidateAndThrowAsync(body, cancellationToken);
        var currency = StorefrontResolver.RequireCurrency(store, body.Currency);
        if (!body.AcceptsDataPolicy)
        {
            throw new StoreDomainException(StoreErrorCodes.ConsentRequired, "The data policy must be accepted.");
        }

        // Un reintento del mismo visitante devuelve el primer recibo, en su propia moneda aunque éste nombre otra.
        var existing = await requests.FindByClientRequestIdAsync(store.TenantId, body.ClientRequestId, cancellationToken);
        if (existing is not null)
        {
            return (Receipt(existing), false);
        }

        var billingPlace = await BuildPlaceAsync(
            store, body.Billing.Country, body.Billing.DepartmentId, body.Billing.CityId, body.Billing.CityName, cancellationToken);
        var shippingPlace = body.Shipping is null
            ? null
            : await BuildPlaceAsync(
                store, body.Shipping.Country, body.Shipping.DepartmentId, body.Shipping.CityId, body.Shipping.CityName, cancellationToken);

        // Cada teléfono, con el país de su propio bloque.
        var billingPhone = RequirePhone(body.Billing.Phone, billingPlace.Country);
        var shippingPhone = body.Shipping is null || shippingPlace is null ? null : RequirePhone(body.Shipping.Phone, shippingPlace.Country);

        var hostname = command.Host.Trim().ToLowerInvariant();
        if (!await captcha.VerifyAsync(body.TurnstileToken.Trim(), command.VisitorIp, hostname, cancellationToken))
        {
            throw new StoreDomainException(StoreErrorCodes.CaptchaInvalid, "The captcha was not passed.");
        }

        var now = clock.UtcNow;
        if (await requests.CountReceivedSinceAsync(store.TenantId, now - CapWindow, cancellationToken) >= options.DailyRequestCap)
        {
            throw new RequestRateLimitedException(
                StoreErrorCodes.DailyCap, "The store reached its daily limit of order requests.", retryAfter: null);
        }

        var (quote, unavailable, origins) = await pricing.PriceWithOriginsAsync(
            store.TenantId, currency, store.PricingMode, body.Lines, cancellationToken);
        if (unavailable.Count > 0 || quote.Lines.Count == 0)
        {
            throw new StoreDomainException(StoreErrorCodes.LineUnavailable, "A line of the cart cannot be sold.");
        }

        var billing = new StoreRequestBilling(
            body.Billing.Name,
            body.Billing.IdentificationType,
            body.Billing.IdentificationNumber,
            body.Billing.Phone,
            billingPhone,
            body.Billing.Email,
            body.Billing.Address,
            billingPlace);
        var shipping = body.Shipping is null || shippingPlace is null || shippingPhone is null
            ? null
            : new StoreRequestShipping(body.Shipping.RecipientName, body.Shipping.Phone, shippingPhone, body.Shipping.Address, shippingPlace);
        var lines = quote.Lines
            .Select(line => new StoreOrderRequestLineInput(
                line.VariantId,
                line.ProductId,
                line.Name,
                line.Quantity,
                line.UnitPrice,
                line.DiscountPercentage,
                origins.GetValueOrDefault(line.VariantId, string.Empty),
                line.DiscountedUnitPrice,
                line.LineTotal))
            .ToList();
        var consent = new StoreConsent(
            now,
            command.VisitorIp,
            store.DataPolicyUrl ?? throw new InvalidOperationException("A published store always has a data policy."),
            store.DataPolicySetAt ?? throw new InvalidOperationException("A published store always has a data policy date."));

        try
        {
            var created = await SaveAsync(store, body, billing, shipping, currency, lines, quote, consent, now, cancellationToken);
            return (Receipt(created), true);
        }
        catch (ResourceConflictException conflict) when (conflict.Code == StoreErrorCodes.DuplicateClientRequest)
        {
            // Dos envíos simultáneos con el mismo id: el que perdió suelta lo suyo (la transacción ya se revirtió, con su número) y
            // devuelve el recibo del que ganó.
            unitOfWork.DiscardPendingChanges();
            var winner = await requests.FindByClientRequestIdAsync(store.TenantId, body.ClientRequestId, cancellationToken)
                ?? throw new InvalidOperationException("A duplicate client request was reported but no request was found.", conflict);
            return (Receipt(winner), false);
        }
    }

    // La transacción vive en su propio método: al propagarse la excepción se libera (y revierte) antes de releer al ganador, porque en
    // Postgres una transacción con una violación de unicidad queda abortada.
    private async Task<StoreOrderRequest> SaveAsync(
        OnlineStore store,
        PublicOrderRequestBody body,
        StoreRequestBilling billing,
        StoreRequestShipping? shipping,
        string currency,
        IReadOnlyList<StoreOrderRequestLineInput> lines,
        PublicCartQuoteDto quote,
        StoreConsent consent,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var number = await numbers.NextAsync(store.TenantId, cancellationToken);
        var request = StoreOrderRequest.Create(
            Guid.CreateVersion7(),
            store.TenantId,
            number,
            body.ClientRequestId,
            billing,
            shipping,
            body.Note,
            currency,
            store.PricingMode,
            lines,
            quote.Subtotal,
            quote.DiscountAmount,
            quote.TaxAmount,
            quote.Total,
            consent,
            now);
        requests.Add(request);
        auditPublisher.PublishAnonymous(
            store.TenantId,
            StoreAuditActions.RequestReceived,
            StoreAuditActions.Resources.OrderRequest,
            request.Id.ToString(),
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return request;
    }

    private async Task<StorePlace> BuildPlaceAsync(
        OnlineStore store, string country, Guid? departmentId, Guid? cityId, string? cityName, CancellationToken cancellationToken)
    {
        var code = StoreCountry.Normalize(country);
        if (!store.SellsTo(code))
        {
            throw new StoreDomainException(StoreErrorCodes.CountryNotSold, "The store does not sell to that country.");
        }

        if (!StoreCountry.IsColombia(code))
        {
            if (departmentId is not null || cityId is not null)
            {
                throw new StoreDomainException(StoreErrorCodes.CityInvalid, "Outside Colombia the city is written, with no ids.");
            }

            return new StorePlace(code, null, null, null, cityName ?? string.Empty);
        }

        if (!string.IsNullOrWhiteSpace(cityName) || departmentId is null || cityId is null)
        {
            throw new StoreDomainException(StoreErrorCodes.CityInvalid, "A Colombian address carries department and city ids.");
        }

        var city = await geography.FindCityAsync(cityId.Value, cancellationToken);
        if (city is null || city.DepartmentId != departmentId.Value)
        {
            throw new StoreDomainException(StoreErrorCodes.CityInvalid, "The city does not belong to the department.");
        }

        return new StorePlace(code, city.DepartmentId, city.DepartmentName, city.CityId, city.CityName);
    }

    private string RequirePhone(string phone, string country) =>
        phones.ToE164(phone.Trim(), country)
        ?? throw new StoreDomainException(StoreErrorCodes.PhoneInvalid, "The phone does not normalize in the country of its block.");

    private static PublicOrderRequestReceiptDto Receipt(StoreOrderRequest request) =>
        new(request.Id, request.DisplayNumber, request.Total, request.Currency);
}
