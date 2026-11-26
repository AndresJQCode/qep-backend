namespace Modules.Support.Domain;

/// <summary>Qué pide el formulario público según el documento (spec §9.3, plan F18).</summary>
public enum SupportFormVariant
{
    /// <summary>Un documento distinto de NIT: nombres y apellidos.</summary>
    Natural,

    /// <summary>NIT: la razón social reemplaza a nombres y apellidos.</summary>
    Nit,

    /// <summary>El tenant ocultó el par documento: no hay vínculo automático con el cliente.</summary>
    NoDocument,

    /// <summary>Trámite anónimo: no se pide nada.</summary>
    Anonymous,
}

/// <summary>Un campo del formulario y si es obligatorio. La clave es la del cuerpo y la del DTO público.</summary>
public sealed record SupportFormField(string Key, bool Required);

/// <summary>
/// La regla de los campos del formulario público, en un solo lugar (spec §9.3 «Dónde vive la regla», plan F18): la configuración
/// pública la manda para dibujar y el validador del radicado la usa para exigir. El frontend no la repite. Un campo oculto no viene
/// en la lista; los obligatorios fijos (celular, país, ciudad, correo y, sin NIT, nombres y apellidos) no los configura el tenant.
/// </summary>
public static class SupportFormFields
{
    public const string DocumentType = "documentType";

    public const string DocumentNumber = "documentNumber";

    public const string FirstNames = "firstNames";

    public const string LastNames = "lastNames";

    public const string BusinessName = "businessName";

    public const string Address = "address";

    public const string LandlinePhone = "landlinePhone";

    public const string MobilePhone = "mobilePhone";

    public const string Country = "country";

    public const string City = "city";

    public const string Email = "email";

    public static IReadOnlyList<SupportFormField> For(SupportFormVariant variant, SupportFormVersion form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return variant switch
        {
            SupportFormVariant.Anonymous => [],

            // Spec §9.3: sin el par documento se piden exactamente nombres, apellidos, celular, país, ciudad y correo.
            SupportFormVariant.NoDocument =>
            [
                new(FirstNames, true),
                new(LastNames, true),
                new(MobilePhone, true),
                new(Country, true),
                new(City, true),
                new(Email, true),
            ],
            SupportFormVariant.Natural => WithDocument(form, [new(FirstNames, true), new(LastNames, true)]),
            SupportFormVariant.Nit => WithDocument(form, [new(BusinessName, form.BusinessNameRequired)]),
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "Unknown form variant."),
        };
    }

    /// <summary>Anónimo gana; sin el par documento, <see cref="SupportFormVariant.NoDocument"/>; con él, NIT (sin distinguir
    /// mayúsculas ni espacios) o cualquier otro tipo, también uno que todavía no llegó.</summary>
    public static SupportFormVariant VariantOf(bool anonymous, bool showDocument, string? documentType)
    {
        if (anonymous)
        {
            return SupportFormVariant.Anonymous;
        }

        if (!showDocument)
        {
            return SupportFormVariant.NoDocument;
        }

        return string.Equals(documentType?.Trim(), TicketRequester.NitDocumentType, StringComparison.OrdinalIgnoreCase)
            ? SupportFormVariant.Nit
            : SupportFormVariant.Natural;
    }

    private static List<SupportFormField> WithDocument(SupportFormVersion form, SupportFormField[] names)
    {
        List<SupportFormField> fields = [new(DocumentType, true), new(DocumentNumber, true), .. names];
        if (form.ShowAddress)
        {
            fields.Add(new(Address, false));
        }

        if (form.ShowLandline)
        {
            fields.Add(new(LandlinePhone, false));
        }

        fields.Add(new(MobilePhone, true));
        fields.Add(new(Country, true));
        fields.Add(new(City, true));
        fields.Add(new(Email, true));
        return fields;
    }
}
