using System.Globalization;
using System.Text;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Billing.Domain;
using Modules.Tenancy.Application;

namespace Modules.Billing.Application;

/// <summary>
/// Las reglas de una fila del Excel de cobros (§3.2b), sobre el texto crudo y con un código por regla: copia de
/// <c>ExcelCustomerRowRules</c>. Sólo lo que la fila sola puede decidir; lo que depende del emparejamiento (el teléfono de una
/// fila que crea el cliente, el encargado, el servicio que ya existe) lo decide <see cref="BillingImportPlanner"/>.
///
/// Un <c>RuleFor</c> por regla, cada uno con su <c>When</c>: encadenados, el <c>When</c> de FluentValidation puede gobernar
/// la cadena entera (el defecto que encontró <c>ExcelCustomerRowRulesTests</c>).
/// </summary>
public sealed class ExcelBillingRowRules : AbstractValidator<ExcelBillingRow>
{
    public ExcelBillingRowRules(IBillingPhoneNormalizer phones, IBillingIdentificationTypes identificationTypes, IClock clock)
    {
        RuleFor(row => row.CustomerName)
            .Must(value => value?.Trim() is { Length: > 0 and <= BillingImportValues.NameMaxLength })
            .WithErrorCode(BillingErrorCodes.ImportRowCustomerNameRequired)
            .WithMessage($"The customer name is required and cannot exceed {BillingImportValues.NameMaxLength} characters.");

        RuleFor(row => row.IdentificationNumber)
            .Must((row, number) => IsBlank(row.IdentificationType) == IsBlank(number))
            .WithErrorCode(BillingErrorCodes.ImportRowIdentificationIncomplete)
            .WithMessage("The identification type and number go together: fill both or neither.");

        RuleFor(row => row.IdentificationType)
            .Must(type => identificationTypes.SupportedWireValues.Contains(type!.Trim(), StringComparer.OrdinalIgnoreCase))
            .WithErrorCode(BillingErrorCodes.ImportRowIdentificationTypeInvalid)
            .WithMessage(_ => $"The identification type must be one of {string.Join(", ", identificationTypes.SupportedWireValues.Order(StringComparer.Ordinal))}.")
            .When(row => !IsBlank(row.IdentificationType));

        RuleFor(row => row.Phone)
            .Must((row, phone) => phones.ToE164(phone, BillingImportValues.CountryOf(row.Country)) is not null)
            .WithErrorCode(BillingErrorCodes.ImportRowPhoneInvalid)
            .WithMessage("The phone is not a valid number for the row's country.")
            .When(row => !IsBlank(row.Phone));

        RuleFor(row => row.Phone)
            .Must(phone => !IsBlank(phone))
            .WithErrorCode(BillingErrorCodes.ImportRowPhoneRequired)
            .WithMessage("The WhatsApp channel needs a phone.")
            .When(row => BillingImportValues.TryParseChannel(row.Channel, out var channel) && channel == BillingChannel.WhatsApp);

        RuleFor(row => row.Email)
            .MaximumLength(BillingImportValues.EmailMaxLength)
            .WithErrorCode(BillingErrorCodes.ImportRowEmailInvalid)
            .WithMessage($"The email cannot exceed {BillingImportValues.EmailMaxLength} characters.")
            .EmailAddress()
            .WithErrorCode(BillingErrorCodes.ImportRowEmailInvalid)
            .WithMessage("The email is not a valid address.")
            .When(row => !IsBlank(row.Email));

        RuleFor(row => row.Email)
            .Must(email => !IsBlank(email))
            .WithErrorCode(BillingErrorCodes.ImportRowEmailRequired)
            .WithMessage("The Email channel needs an email.")
            .When(row => BillingImportValues.TryParseChannel(row.Channel, out var channel) && channel == BillingChannel.Email);

        RuleFor(row => row.Country)
            .Must(country => country!.Trim() is { Length: 2 } code && code.All(char.IsAsciiLetter) && phones.IsKnownCountry(code.ToUpperInvariant()))
            .WithErrorCode(BillingErrorCodes.ImportRowCountryInvalid)
            .WithMessage("The country must be a known ISO 3166-1 alpha-2 code.")
            .When(row => !IsBlank(row.Country));

        RuleFor(row => row.ServiceName)
            .Must(value => value?.Trim() is { Length: > 0 and <= BillingService.NameMaxLength })
            .WithErrorCode(BillingErrorCodes.ImportRowServiceNameRequired)
            .WithMessage($"The service name is required and cannot exceed {BillingService.NameMaxLength} characters.");

        RuleFor(row => row.Price)
            .Must(price => BillingImportValues.TryParsePrice(price, out _))
            .WithErrorCode(BillingErrorCodes.ImportRowPriceInvalid)
            .WithMessage("The price must be a number greater than zero with at most two decimals.");

        RuleFor(row => row.Currency)
            .Must(Currencies.IsSupported)
            .WithErrorCode(BillingErrorCodes.ImportRowCurrencyUnknown)
            .WithMessage("The currency is not in the catalogue.")
            .When(row => !IsBlank(row.Currency));

        RuleFor(row => row.Cycle)
            .Must(cycle => BillingImportValues.TryParseCycle(cycle, out _))
            .WithErrorCode(BillingErrorCodes.ImportRowCycleInvalid)
            .WithMessage($"The cycle must be one of {string.Join(", ", BillingImportColumns.Cycles)}.");

        RuleFor(row => row.DueDate)
            .Must(date => BillingImportValues.TryParseDate(date, out var parsed) && IsReasonable(parsed, clock))
            .WithErrorCode(BillingErrorCodes.ImportRowDueDateInvalid)
            .WithMessage(
                $"The next due date must be a date between {ServiceRules.EarliestFirstDueDate:yyyy-MM-dd} and "
                + $"{ServiceRules.LatestFirstDueDateYearsAhead} years from today.");

        RuleFor(row => row.Channel)
            .Must(channel => BillingImportValues.TryParseChannel(channel, out _))
            .WithErrorCode(BillingErrorCodes.ImportRowChannelInvalid)
            .WithMessage($"The channel must be one of {string.Join(", ", BillingImportColumns.Channels)}.")
            .When(row => !IsBlank(row.Channel));

        RuleFor(row => row.AssigneeEmail)
            .Must(email => !IsBlank(email))
            .WithErrorCode(BillingErrorCodes.ImportRowAssigneeRequired)
            .WithMessage("The assignee email is required.");
    }

    private static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);

    // El mismo rango que el POST de servicios (ServiceRules): un ancla de hace siglos sería un request de miles de cobros.
    private static bool IsReasonable(DateOnly date, IClock clock)
    {
        var latest = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime).AddYears(ServiceRules.LatestFirstDueDateYearsAhead);
        return date >= ServiceRules.EarliestFirstDueDate && date <= latest;
    }
}

/// <summary>
/// Una fila que ya pasó <see cref="ExcelBillingRowRules"/>, con sus valores interpretados: el teléfono en E.164, el correo
/// en minúsculas, el ciclo y el canal como enums. <c>Currency</c> null es «la del tenant»; <c>Channel</c> null es «el que
/// resuelva el plan» (WhatsApp con teléfono, si no Email).
/// </summary>
public sealed record BillingImportRow(
    int RowNumber,
    string CustomerName,
    string? IdentificationType,
    string? IdentificationNumber,
    string? Phone,
    string? PhoneE164,
    string? Email,
    string Country,
    string ServiceName,
    decimal Price,
    string? Currency,
    BillingCycle Cycle,
    DateOnly FirstDueDate,
    BillingChannel? Channel,
    string AssigneeEmail)
{
    /// <summary>
    /// La clave de emparejamiento (§3.2b): documento, si no E.164, si no correo en minúsculas. <c>null</c> si la fila no trae
    /// ninguno; una fila así no se deduplica ni se puede crear (crear exige teléfono).
    /// </summary>
    public string? CustomerKey =>
        IdentificationType is not null && IdentificationNumber is not null
            ? $"document:{IdentificationType.ToUpperInvariant()}:{IdentificationNumber}"
            : PhoneE164 is not null
                ? $"phone:{PhoneE164}"
                : Email is not null ? $"email:{Email.ToLowerInvariant()}" : null;

    public string ServiceKey => BillingImportValues.NormalizeServiceName(ServiceName);

    /// <summary>Interpreta una fila que ya pasó las reglas: si no las pasó, lanza.</summary>
    public static BillingImportRow From(ExcelBillingRow row, IBillingPhoneNormalizer phones)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(phones);
        var country = BillingImportValues.CountryOf(row.Country);
        if (!BillingImportValues.TryParsePrice(row.Price, out var price)
            || !BillingImportValues.TryParseCycle(row.Cycle, out var cycle)
            || !BillingImportValues.TryParseDate(row.DueDate, out var dueDate))
        {
            throw new InvalidOperationException($"Row {row.RowNumber} did not pass ExcelBillingRowRules.");
        }

        BillingChannel? channel = BillingImportValues.TryParseChannel(row.Channel, out var parsed) ? parsed : null;
        return new BillingImportRow(
            row.RowNumber,
            row.CustomerName!.Trim(),
            BillingImportValues.Blank(row.IdentificationType)?.ToUpperInvariant(),
            BillingImportValues.Blank(row.IdentificationNumber),
            BillingImportValues.Blank(row.Phone),
            phones.ToE164(BillingImportValues.Blank(row.Phone), country),
            BillingImportValues.Blank(row.Email)?.ToLowerInvariant(),
            country,
            row.ServiceName!.Trim(),
            price,
            BillingImportValues.Blank(row.Currency) is { } currency ? Currencies.Normalize(currency) : null,
            cycle,
            dueDate,
            channel,
            row.AssigneeEmail!.Trim().ToLowerInvariant());
    }
}

/// <summary>La interpretación de las celdas, compartida por las reglas y por <see cref="BillingImportRow.From"/>.</summary>
internal static class BillingImportValues
{
    /// <summary>§3.2b: <c>Cliente</c> vacío o de más de 150 es <c>customer_name_required</c>.</summary>
    public const int NameMaxLength = 150;

    public const int EmailMaxLength = 254;

    public const string DefaultCountry = "CO";

    // ISO primero (lo que escribe el lector cuando la celda es fecha); día primero es como se escribe a mano en Colombia.
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy"];

    // numeric(18,2): 16 dígitos enteros como mucho.
    private const decimal PriceCeiling = 10_000_000_000_000_000m;

    public static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>El país de la fila en mayúsculas; vacío es Colombia (§3.2b, «país de la fila o CO»).</summary>
    public static string CountryOf(string? value) => Blank(value)?.ToUpperInvariant() ?? DefaultCountry;

    public static bool TryParseCycle(string? text, out BillingCycle cycle)
    {
        switch (Fold(text))
        {
            case "mensual":
                cycle = BillingCycle.Monthly;
                return true;
            case "semestral":
                cycle = BillingCycle.Semiannual;
                return true;
            case "anual":
                cycle = BillingCycle.Annual;
                return true;
            default:
                cycle = default;
                return false;
        }
    }

    public static bool TryParseChannel(string? text, out BillingChannel channel)
    {
        switch (Fold(text))
        {
            case "whatsapp":
                channel = BillingChannel.WhatsApp;
                return true;
            case "email":
                channel = BillingChannel.Email;
                return true;
            default:
                channel = default;
                return false;
        }
    }

    /// <summary>
    /// Punto decimal y como mucho dos decimales. «150.000» tiene tres, así que se rechaza en vez de leerse como 150: en
    /// Colombia el punto separa miles, y adivinar cuál quiso decir cobraría mil veces menos.
    /// </summary>
    public static bool TryParsePrice(string? text, out decimal price) =>
        decimal.TryParse(Blank(text), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out price)
        && price > 0m
        && price.Scale <= ServiceRules.PriceScale
        && price < PriceCeiling;

    public static bool TryParseDate(string? text, out DateOnly date) =>
        DateOnly.TryParseExact(Blank(text), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    /// <summary>Sin mayúsculas ni espacios extra (§3.2b, <c>duplicate_service</c> y <c>service_exists</c>).</summary>
    public static string NormalizeServiceName(string name) =>
        string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    /// <summary>Sin tildes, sin mayúsculas y recortado: «Ánual» y «ANUAL» son «anual».</summary>
    public static string? Fold(string? text)
    {
        var trimmed = Blank(text);
        if (trimmed is null)
        {
            return null;
        }

        var decomposed = trimmed.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }
}
