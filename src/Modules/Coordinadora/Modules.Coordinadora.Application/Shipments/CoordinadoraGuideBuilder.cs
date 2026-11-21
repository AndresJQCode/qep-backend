using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Modules.Coordinadora.Domain;

namespace Modules.Coordinadora.Application.Shipments;

/// <summary>Lo que la persona corrige para este envío sin tocar la configuración (spec 2026-11-21 §5.3): el paquete y el valor declarado.</summary>
public sealed record CoordinadoraShipmentOverrides(CoordinadoraPackage? Package, decimal? DeclaredValue);

/// <summary>El resultado del armador: la guía y la cotización listas, o todo lo que falta.</summary>
public abstract record CoordinadoraBuildResult
{
    private CoordinadoraBuildResult()
    {
    }

    /// <summary><paramref name="GuideJson"/> es el <c>p</c> de <c>Guias_generarGuia</c> y <paramref name="QuoteJson"/> el de
    /// <c>Cotizador.cotizar</c>, los dos sin credenciales.</summary>
    public sealed record Ready(string GuideJson, string QuoteJson, CoordinadoraPackage Package, decimal DeclaredValue) : CoordinadoraBuildResult;

    public sealed record NotReady(IReadOnlyList<CoordinadoraMissing> Missing) : CoordinadoraBuildResult;
}

/// <summary>
/// Spec 2026-11-21 §5.3: junta <b>todos</b> los faltantes y, si no hay ninguno, arma el <c>p</c> de la guía y el de la cotización con los
/// nombres de campo del contrato (§3). Puro: sin reloj ni repositorios. Los valores van como texto, que es como el cliente PHP de
/// referencia los manda; <c>peso</c> con punto decimal y cultura invariante. <c>recaudos</c> va como el valor en texto (V-3).
/// </summary>
public static class CoordinadoraGuideBuilder
{
    public const int ContentMaxLength = 200;
    public const int ObservationsMaxLength = 500;
    public const string PackageName = "Paquete";
    public const string PrintedState = "IMPRESO";

    private static readonly JsonSerializerOptions Compact = new(JsonSerializerDefaults.General) { WriteIndented = false };

    public static CoordinadoraBuildResult Build(
        CoordinadoraShipmentInput input,
        CoordinadoraSettings? settings,
        CoordinadoraCityRef? recipientCity,
        CoordinadoraCityRef? senderCity,
        string? senderNit,
        CoordinadoraShipmentOverrides? overrides)
    {
        ArgumentNullException.ThrowIfNull(input);
        var missing = new List<CoordinadoraMissing>();

        if (input.IsCancelled)
        {
            missing.Add(CoordinadoraMissing.Of(CoordinadoraMissing.OrderCancelled));
        }

        if (input.IsStorePickup)
        {
            missing.Add(CoordinadoraMissing.Of(CoordinadoraMissing.StorePickup));
        }

        if (!input.RequiresShipping)
        {
            missing.Add(CoordinadoraMissing.Of(CoordinadoraMissing.NothingToShip));
        }

        if (!string.IsNullOrWhiteSpace(input.CarrierName) && !CoordinadoraText.IsCoordinadora(input.CarrierName))
        {
            missing.Add(new CoordinadoraMissing(CoordinadoraMissing.CarrierMismatch, null, input.CarrierName.Trim()));
        }

        var recipient = input.Recipient;
        if (string.IsNullOrWhiteSpace(recipient.Name))
        {
            missing.Add(CoordinadoraMissing.Of(CoordinadoraMissing.RecipientName));
        }

        if (string.IsNullOrWhiteSpace(recipient.Phone))
        {
            missing.Add(CoordinadoraMissing.Of(CoordinadoraMissing.RecipientPhone));
        }

        if (string.IsNullOrWhiteSpace(recipient.Address))
        {
            missing.Add(CoordinadoraMissing.Of(CoordinadoraMissing.RecipientAddress));
        }

        // Coordinadora nacional: sólo un municipio colombiano de Geography. Fuera de Colombia, o sin ciudad, no hay código que mandar.
        if (recipientCity is null || !IsColombian(recipient.Country))
        {
            missing.Add(CoordinadoraMissing.Of(CoordinadoraMissing.RecipientCity));
        }
        else if (!recipientCity.IsServed)
        {
            missing.Add(new CoordinadoraMissing(CoordinadoraMissing.RecipientCityNotServed, recipientCity.CityId.ToString(), recipientCity.Name));
        }

        if (settings is null)
        {
            missing.Add(CoordinadoraMissing.Of(CoordinadoraMissing.ConnectionMissing));
        }
        else
        {
            missing.AddRange(settings.MissingForShipping().Select(CoordinadoraMissing.Of));
            if (settings.SenderCityId is not null && senderCity is { IsServed: false })
            {
                missing.Add(new CoordinadoraMissing(CoordinadoraMissing.SenderCityNotServed, senderCity.CityId.ToString(), senderCity.Name));
            }
            else if (settings.SenderCityId is not null && senderCity is null && !missing.Exists(value => value.Code == CoordinadoraMissing.SenderIncomplete))
            {
                missing.Add(CoordinadoraMissing.Of(CoordinadoraMissing.SenderIncomplete));
            }
        }

        if (missing.Count > 0)
        {
            return new CoordinadoraBuildResult.NotReady(missing);
        }

        var package = overrides?.Package ?? settings!.DefaultPackage!;
        var declaredValue = Math.Max(
            decimal.Round(overrides?.DeclaredValue ?? input.ProductsTotal, 0, MidpointRounding.AwayFromZero),
            settings!.DeclaredValueMinimum);
        var guide = GuideBody(input, settings, recipientCity!, senderCity!, senderNit, package, declaredValue);
        var quote = QuoteBody(settings, recipientCity!, senderCity!, package, declaredValue);
        return new CoordinadoraBuildResult.Ready(guide.ToJsonString(Compact), quote.ToJsonString(Compact), package, declaredValue);
    }

    /// <summary>Las líneas como «3 x Aceite esencial, 1 x Vela», recortado.</summary>
    public static string ContentOf(IReadOnlyList<CoordinadoraShipmentLine> lines)
    {
        var text = string.Join(
            ", ",
            lines.Select(line => $"{FormatQuantity(line.Quantity)} x {line.Name.Trim()}"));
        return CoordinadoraText.Cap(text, ContentMaxLength) ?? "Mercancía";
    }

    private static JsonObject GuideBody(
        CoordinadoraShipmentInput input,
        CoordinadoraSettings settings,
        CoordinadoraCityRef recipientCity,
        CoordinadoraCityRef senderCity,
        string? senderNit,
        CoordinadoraPackage package,
        decimal declaredValue)
    {
        var recipient = input.Recipient;
        var collect = input.CashOnDelivery && settings.CollectOnDelivery;
        return new JsonObject
        {
            ["codigo_remision"] = string.Empty,
            ["fecha"] = input.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["id_remitente"] = "0",
            ["nit_remitente"] = senderNit?.Trim() ?? string.Empty,
            ["nombre_remitente"] = settings.SenderName,
            ["direccion_remitente"] = settings.SenderAddress,
            ["telefono_remitente"] = settings.SenderPhone,
            ["ciudad_remitente"] = senderCity.CoordinadoraCode,
            ["nit_destinatario"] = DigitsOnly(recipient.Identification) ?? "0",
            ["div_destinatario"] = "0",
            ["nombre_destinatario"] = recipient.Name!.Trim(),
            ["direccion_destinatario"] = recipient.Address!.Trim(),
            ["ciudad_destinatario"] = recipientCity.CoordinadoraCode,
            ["telefono_destinatario"] = recipient.Phone!.Trim(),
            ["valor_declarado"] = Whole(declaredValue),
            ["codigo_cuenta"] = settings.AccountCode,
            ["codigo_producto"] = settings.ProductCode,
            ["nivel_servicio"] = settings.ServiceLevel!.Value.ToString(CultureInfo.InvariantCulture),
            ["linea"] = string.Empty,
            ["contenido"] = ContentOf(input.Lines),
            ["referencia"] = input.OrderNumber,
            ["observaciones"] = CoordinadoraText.Cap(input.Notes, ObservationsMaxLength) ?? string.Empty,
            ["estado"] = PrintedState,
            ["detalle"] = new JsonArray(Item(settings, package, input.OrderNumber)),
            ["cuenta_contable"] = string.Empty,
            ["centro_costos"] = string.Empty,
            ["recaudos"] = collect ? Whole(input.NetTotal) : string.Empty,
            ["margen_izquierdo"] = string.Empty,
            ["margen_superior"] = string.Empty,
            ["id_rotulo"] = "0",
            ["usuario_vmi"] = string.Empty,
            ["formato_impresion"] = settings.LabelFormat ?? string.Empty,
            ["atributo1_nombre"] = string.Empty,
            ["atributo1_valor"] = string.Empty,
            ["notificaciones"] = new JsonObject(),
            ["atributos_retorno"] = new JsonObject
            {
                ["nit"] = string.Empty,
                ["div"] = string.Empty,
                ["nombre"] = string.Empty,
                ["direccion"] = string.Empty,
                ["codigo_ciudad"] = string.Empty,
                ["telefono"] = string.Empty,
            },
            ["nro_doc_radicados"] = string.Empty,
            ["nro_sobre"] = string.Empty,
        };
    }

    private static JsonObject QuoteBody(
        CoordinadoraSettings settings,
        CoordinadoraCityRef recipientCity,
        CoordinadoraCityRef senderCity,
        CoordinadoraPackage package,
        decimal declaredValue) =>
        new()
        {
            ["div"] = settings.Div ?? string.Empty,
            ["cuenta"] = settings.AccountCode,
            ["producto"] = settings.ProductCode,
            ["origen"] = senderCity.CoordinadoraCode,
            ["destino"] = recipientCity.CoordinadoraCode,
            ["valoracion"] = Whole(declaredValue),
            ["nivel_servicio"] = new JsonArray(JsonValue.Create(settings.ServiceLevel!.Value)),
            ["detalle"] = new JsonObject { ["item"] = new JsonArray(Item(settings, package, null)) },
        };

    private static JsonObject Item(CoordinadoraSettings settings, CoordinadoraPackage package, string? reference)
    {
        var item = new JsonObject
        {
            ["ubl"] = settings.PackagingType.ToString(CultureInfo.InvariantCulture),
            ["alto"] = package.HeightCm.ToString(CultureInfo.InvariantCulture),
            ["ancho"] = package.WidthCm.ToString(CultureInfo.InvariantCulture),
            ["largo"] = package.LengthCm.ToString(CultureInfo.InvariantCulture),
            ["peso"] = package.WeightKg.ToString("0.##", CultureInfo.InvariantCulture),
            ["unidades"] = package.Units.ToString(CultureInfo.InvariantCulture),
        };
        if (reference is not null)
        {
            item["referencia"] = reference;
            item["nombre_empaque"] = PackageName;
        }

        return item;
    }

    private static string Whole(decimal value) =>
        decimal.Round(value, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);

    private static string FormatQuantity(decimal quantity) =>
        quantity == decimal.Truncate(quantity)
            ? quantity.ToString("0", CultureInfo.InvariantCulture)
            : quantity.ToString("0.##", CultureInfo.InvariantCulture);

    // El NIT o la cédula sólo con dígitos: lo que va después del guion (el dígito de verificación) se corta, como hace Siigo.
    private static string? DigitsOnly(string? identification)
    {
        if (string.IsNullOrWhiteSpace(identification))
        {
            return null;
        }

        var head = identification.Split('-')[0];
        var digits = new string(head.Where(char.IsAsciiDigit).ToArray());
        return digits.Length > 0 ? digits : null;
    }

    // Un país vacío es el de la ficha vieja: Colombia.
    private static bool IsColombian(string? country) =>
        string.IsNullOrWhiteSpace(country) || string.Equals(country, "CO", StringComparison.OrdinalIgnoreCase);
}
