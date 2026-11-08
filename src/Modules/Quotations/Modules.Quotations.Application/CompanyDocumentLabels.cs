namespace Modules.Quotations.Application;

/// <summary>
/// Cómo se rotula el documento de la empresa emisora en el PDF. Quotations no referencia
/// Companies, así que la tabla se indexa por el código de wire que llega por
/// <see cref="IQuotationCompanyLookup"/>. Tiene que coincidir con <c>companyDocumentPrefix</c>
/// del frontend (<c>qep-frontend/src/components/company-document-types.ts</c>): si cambias una,
/// cambia la otra.
/// </summary>
public static class CompanyDocumentLabels
{
    /// <summary>Null es una empresa anterior al tipo: siempre fue NIT. Un código que no conoces
    /// se imprime tal cual, para no esconder un tipo nuevo detrás de un rótulo inventado.</summary>
    public static string For(string? wire) => wire switch
    {
        null => "NIT",
        "NIT" => "NIT",
        "CC" => "CC",
        "CE" => "CE",
        "PASAPORTE" => "Pasaporte",
        "NIF" => "NIF",
        "NIE" => "NIE",
        "TAX_ID" => "Identificación tributaria",
        _ => wire,
    };
}
