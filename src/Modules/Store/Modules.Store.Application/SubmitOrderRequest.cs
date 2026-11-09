using BuildingBlocks.Application;
using BuildingBlocks.Domain.Identification;
using FluentValidation;
using Microsoft.Extensions.Logging;
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

/// <summary>Crear la cuenta de comprador desde el pedido: el email de ingreso es el de facturación y el perfil, los bloques del pedido.</summary>
/// <param name="AcceptsDataPolicy">Explícito (D5): sin un <c>true</c> no hay prueba de que se aceptó la política de la cuenta.</param>
public sealed record PublicBuyerSignupInput(string Password, bool AcceptsDataPolicy);

/// <summary>El cuerpo de <c>POST /order-requests</c>. La moneda no se valida acá: una moneda ausente es un código de la tienda y no <c>validation.failed</c>.</summary>
/// <param name="CreateAccount">Opcional; <c>null</c> = no crear cuenta.</param>
/// <param name="UpdateProfile">Reemplaza el perfil guardado del comprador de la sesión con los bloques del pedido, después del commit.</param>
public sealed record PublicOrderRequestBody(
    Guid ClientRequestId,
    string TurnstileToken,
    string? Currency,
    PublicBillingInput Billing,
    PublicShippingInput? Shipping,
    string? Note,
    bool AcceptsDataPolicy,
    IReadOnlyList<PublicCartLineRequest> Lines,
    PublicBuyerSignupInput? CreateAccount = null,
    bool UpdateProfile = false);

/// <param name="Number">El consecutivo con formato de pantalla: <c>T-0001</c>.</param>
/// <param name="BuyerSessionRejected"><c>true</c> sólo si se pidió <c>updateProfile</c> y la sesión no sirve: el BFF borra la cookie.</param>
public sealed record PublicOrderRequestReceiptDto(
    Guid Id, string Number, decimal Total, string Currency, bool BuyerSessionRejected = false);

/// <param name="VisitorIp">La IP que declaró el storefront con su clave; es la del consentimiento, nunca la del par directo.</param>
/// <param name="BuyerSessionToken">El token crudo del header de sesión de comprador; sólo se usa con <c>updateProfile</c>.</param>
public sealed record SubmitOrderRequestCommand(
    string Host, string VisitorIp, PublicOrderRequestBody Body, string? BuyerSessionToken = null)
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

    // Copia de BuyerPasswordRules (Store no ve Buyers); BuyerParityTests (Bootstrapper.UnitTests) las ata.
    public const int BuyerPasswordMinLength = 10;

    public const int BuyerPasswordMaxLength = 128;

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

        // La contraseña se juzga con el resto del cuerpo, antes de aceptar el pedido: es un error del formulario que se corrige.
        RuleFor(body => body.CreateAccount!).SetValidator(new CreateAccountValidator()).When(body => body.CreateAccount is not null);
        // D6: con sesión no hay cuenta que crear.
        RuleFor(body => body.CreateAccount)
            .Null()
            .When(body => body.UpdateProfile)
            .WithMessage("No se crea una cuenta con una sesión iniciada.");
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
            RuleFor(b => b.Name).NotEmpty().MaximumLength(StoreRequestBilling.NameMaxLength);
            RuleFor(b => b.IdentificationType)
                .NotEmpty()
                .Must(type => type is null || DocumentTypesByCountry.IsKnown(type))
                .WithMessage("El tipo de documento no es válido.");
            // Cadena aparte para no callar la regla del código conocido; sólo corre con país de forma válida.
            RuleFor(b => b.IdentificationType)
                .Must((b, type) => DocumentTypesByCountry.IsAllowed(type, b.Country!))
                .WithMessage("El tipo de documento no es válido para el país.")
                .When(b => DocumentTypesByCountry.IsKnown(b.IdentificationType) && !string.IsNullOrWhiteSpace(b.Country) && CountryShape(b.Country));
            RuleFor(b => b.IdentificationNumber).NotEmpty().MaximumLength(StoreRequestBilling.IdentificationNumberMaxLength);
            RuleFor(b => b.Phone).NotEmpty().MaximumLength(PhoneMaxLength);
            RuleFor(b => b.Email).NotEmpty().MaximumLength(StoreRequestBilling.EmailMaxLength).EmailAddress();
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

    private sealed class CreateAccountValidator : AbstractValidator<PublicBuyerSignupInput>
    {
        public CreateAccountValidator()
        {
            // Una sola regla, como BuyerPasswordRules: vacía, corta y larga dan un único error.
            RuleFor(input => input.Password)
                .Must(password => password is { Length: >= BuyerPasswordMinLength and <= BuyerPasswordMaxLength })
                .WithMessage($"La contraseña debe tener entre {BuyerPasswordMinLength} y {BuyerPasswordMaxLength} caracteres.");
            RuleFor(input => input.AcceptsDataPolicy)
                .Equal(true)
                .WithMessage("Para crear la cuenta hay que aceptar la política de datos.");
        }
    }

    private sealed class ShippingValidator : AbstractValidator<PublicShippingInput>
    {
        public ShippingValidator()
        {
            RuleFor(s => s.RecipientName).MaximumLength(StoreRequestShipping.RecipientNameMaxLength);
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

public sealed partial class SubmitOrderRequestHandler(
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
    IValidator<PublicOrderRequestBody> validator,
    IStoreBuyerAccounts buyers,
    IStoreOutbox outbox,
    ILogger<SubmitOrderRequestHandler> logger)
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
        var (store, tenant) = await resolver.ResolveAsync(command.Host, cancellationToken);
        await validator.ValidateAndThrowAsync(body, cancellationToken);
        var currency = StorefrontResolver.RequireCurrency(store, body.Currency);
        if (!body.AcceptsDataPolicy)
        {
            throw new StoreDomainException(StoreErrorCodes.ConsentRequired, "The data policy must be accepted.");
        }

        // Un reintento del mismo visitante devuelve el primer recibo, en su propia moneda aunque éste nombre otra, sin alta de cuenta
        // ni perfil (buyerSessionRejected queda en false).
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
            store.TenantId, currency, store.PricingMode, body.Lines, cancellationToken, nameUnavailable: false);
        if (unavailable.Count > 0 || quote.Lines.Count == 0)
        {
            throw new StoreDomainException(StoreErrorCodes.LineUnavailable, "A line of the cart cannot be sold.");
        }

        // El único costo de la cuenta en el request: el hash, antes de abrir la transacción, para que la contraseña en claro nunca se
        // persista (D7). Lo que falle acá sólo se registra y el pedido sigue sin alta.
        var signup = PrepareSignup(body, hostname, store.Name ?? tenant.DisplayName, command.VisitorIp);

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
        // La política se congela tal como está al enviar; una tienda sin política deja las dos en null.
        var consent = new StoreConsent(now, command.VisitorIp, store.DataPolicyUrl, store.DataPolicySetAt);

        StoreOrderRequest created;
        try
        {
            created = await SaveAsync(
                store, body, billing, shipping, currency, lines, quote, consent, signup, now, cancellationToken);
        }
        catch (ResourceConflictException conflict) when (conflict.Code == StoreErrorCodes.DuplicateClientRequest)
        {
            // Dos envíos simultáneos con el mismo id: el que perdió suelta lo suyo (la transacción ya se revirtió, con su número y su
            // mensaje de alta) y devuelve el recibo del que ganó, sin tocar el perfil: lo hace sólo quien guardó la solicitud.
            unitOfWork.DiscardPendingChanges();
            var winner = await requests.FindByClientRequestIdAsync(store.TenantId, body.ClientRequestId, cancellationToken)
                ?? throw new InvalidOperationException("A duplicate client request was reported but no request was found.", conflict);
            return (Receipt(winner), false);
        }

        var receipt = Receipt(created);
        if (body.UpdateProfile)
        {
            receipt = receipt with
            {
                BuyerSessionRejected = await ProfileUpdateRejectedAsync(hostname, command.BuyerSessionToken, body, created.Id),
            };
        }

        return (receipt, true);
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
        PendingSignup? signup,
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
        // El alta viaja en la misma transacción que la solicitud: el perdedor de la carrera por clientRequestId la revierte con lo suyo.
        if (signup is not null)
        {
            outbox.Add(
                StoreIntegrationEvents.BuyerSignupRequested,
                new StoreBuyerSignupRequestedPayload(
                    request.Id,
                    store.TenantId,
                    body.ClientRequestId,
                    signup.StoreHost,
                    signup.StoreName,
                    signup.VisitorIp,
                    signup.Email,
                    signup.PasswordHash,
                    body.Billing,
                    body.Shipping),
                now);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return request;
    }

    private PendingSignup? PrepareSignup(PublicOrderRequestBody body, string storeHost, string storeName, string visitorIp)
    {
        if (body.CreateAccount is null)
        {
            return null;
        }

        // La misma normalización que la cuenta (BuyerEmail): un email que no la pasa no da de alta nada, y el pedido sigue.
        if (!StoreBuyerSignup.TryNormalizeEmail(body.Billing.Email, out var email))
        {
            LogSignupSkippedInvalidEmail(logger);
            return null;
        }

        try
        {
            return new PendingSignup(storeHost, storeName, visitorIp, email, buyers.HashPassword(body.CreateAccount.Password));
        }
        catch (Exception exception)
        {
            LogSignupHashFailed(logger, exception.GetType().Name);
            return null;
        }
    }

    // Después del commit: el pedido ya está aceptado y nada de esto lo cambia. Sólo una sesión que no sirve marca el recibo; un error
    // se registra (sin email ni token) y el recibo queda intacto.
    private async Task<bool> ProfileUpdateRejectedAsync(
        string storeHost, string? sessionToken, PublicOrderRequestBody body, Guid requestId)
    {
        try
        {
            // Sin el token del request: el pedido ya commiteó y un corte del visitante no debe dejar el perfil a medias.
            if (await buyers.ReplaceProfileAsync(storeHost, sessionToken, body.Billing, body.Shipping, CancellationToken.None))
            {
                return false;
            }

            LogProfileSessionRejected(logger, requestId);
            return true;
        }
        catch (Exception exception)
        {
            LogProfileUpdateFailed(logger, requestId, exception.GetType().Name);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Order request {RequestId} asked to update the buyer profile with a session that is not valid on its store; it was ignored.")]
    private static partial void LogProfileSessionRejected(ILogger logger, Guid requestId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Updating the buyer profile after order request {RequestId} failed with {ExceptionType}; the order stands.")]
    private static partial void LogProfileUpdateFailed(ILogger logger, Guid requestId, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Checkout buyer signup skipped: the billing email does not normalize.")]
    private static partial void LogSignupSkippedInvalidEmail(ILogger logger);

    // Sólo el tipo de la excepción: el mensaje de un hasher ajeno no se loguea por si arrastra la entrada.
    [LoggerMessage(Level = LogLevel.Error, Message = "Checkout buyer signup skipped: hashing the password failed with {ExceptionType}.")]
    private static partial void LogSignupHashFailed(ILogger logger, string exceptionType);

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

    // Lo que el alta necesita, calculado antes de la transacción. Nunca la contraseña en claro.
    private sealed record PendingSignup(string StoreHost, string StoreName, string VisitorIp, string Email, string PasswordHash);
}
