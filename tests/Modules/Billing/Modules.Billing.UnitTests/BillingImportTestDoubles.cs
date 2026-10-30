using BuildingBlocks.Application;
using Modules.Billing.Application;
using Modules.Billing.Domain;

namespace Modules.Billing.UnitTests;

/// <summary>Dobles de la importación (§3.2b): reloj fijo, normalizador de teléfonos predecible y los tipos de documento de Customers.</summary>
internal static class BillingImportTestDoubles
{
    public static readonly DateTimeOffset Now = new(2026, 3, 10, 13, 0, 0, TimeSpan.Zero);

    public sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    /// <summary>
    /// Colombia: diez dígitos → <c>+57</c>. Con «+» delante, el número tal cual si trae al menos ocho dígitos. El resto no
    /// normaliza. Países conocidos: CO, US, MX, EC.
    /// </summary>
    public sealed class FakePhones : IBillingPhoneNormalizer
    {
        private static readonly string[] Known = ["CO", "US", "MX", "EC"];

        public string? ToE164(string? phone, string? country)
        {
            if (string.IsNullOrWhiteSpace(phone))
            {
                return null;
            }

            var digits = new string(phone.Where(char.IsAsciiDigit).ToArray());
            if (phone.TrimStart().StartsWith('+'))
            {
                return digits.Length >= 8 ? "+" + digits : null;
            }

            return (country ?? "CO") == "CO" && digits.Length == 10 ? "+57" + digits : null;
        }

        public bool IsKnownCountry(string country) => Known.Contains(country.Trim().ToUpperInvariant());
    }

    public sealed class FakeIdentificationTypes : IBillingIdentificationTypes
    {
        public IReadOnlyCollection<string> SupportedWireValues { get; } = ["NIT", "CC", "CE", "PASAPORTE"];
    }

    public static ExcelBillingRow ValidRow(int rowNumber = 2) => new(
        rowNumber,
        "Panadería La Esquina",
        "CC",
        "1020304050",
        "300 123 4567",
        "pan@example.com",
        "CO",
        "Hosting",
        "150000",
        "COP",
        "Mensual",
        "2026-04-10",
        "WhatsApp",
        "ana@example.com");

    public static BillingImportRow ImportRow(
        int rowNumber = 2,
        string serviceName = "Hosting",
        string? identificationType = "CC",
        string? identificationNumber = "1020304050",
        string? phoneE164 = "+573001234567",
        string? email = "pan@example.com",
        BillingChannel? channel = null,
        string assigneeEmail = "ana@example.com",
        string firstDueDate = "2026-04-10",
        BillingCycle cycle = BillingCycle.Monthly) => new(
        rowNumber,
        "Panadería La Esquina",
        identificationType,
        identificationNumber,
        phoneE164 is null ? null : "300 123 4567",
        phoneE164,
        email,
        "CO",
        serviceName,
        150000m,
        null,
        cycle,
        DateOnly.ParseExact(firstDueDate, "yyyy-MM-dd"),
        channel,
        assigneeEmail);
}
