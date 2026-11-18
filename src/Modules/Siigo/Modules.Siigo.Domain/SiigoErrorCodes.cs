namespace Modules.Siigo.Domain;

/// <summary>Los códigos de error de Siigo (<c>siigo.*</c>). Son contrato con el frontend, que traduce por ellos: no se renombran. El
/// status lo decide el mapeo central (<c>ApiExceptionHandler</c>), nunca un handler.</summary>
public static class SiigoErrorCodes
{
    /// <summary>409: otra conexión del tenant ya factura con esa empresa (<c>IX_settings_tenant_company</c>).</summary>
    public const string SettingsCompanyTaken = "siigo.settings.company_taken";

    /// <summary>422: la empresa no existe en el tenant o está inactiva.</summary>
    public const string SettingsCompanyInvalid = "siigo.settings.company_invalid";

    /// <summary>422: una lista de vínculos repite su clave (porcentaje, tasa o cuenta).</summary>
    public const string SettingsDuplicateLink = "siigo.settings.duplicate_link";

    /// <summary>422: los días de crédito no van sin su forma de pago, o están fuera de rango.</summary>
    public const string SettingsCreditInvalid = "siigo.settings.credit_invalid";

    /// <summary>422: un valor de la configuración pasa del largo de su columna (los validadores lo frenan antes con el campo).</summary>
    public const string SettingsValueTooLong = "siigo.settings.value_too_long";

    /// <summary>422: la variante que se quiere vincular no existe en el catálogo del tenant.</summary>
    public const string ProductVariantNotFound = "siigo.product.variant_not_found";

    /// <summary>422: Siigo no tiene un producto activo con ese id y ese código (el vínculo manual se valida contra Siigo).</summary>
    public const string ProductNotFoundInSiigo = "siigo.product.not_found_in_siigo";

    /// <summary>409: la conexión no se puede usar (pausada, con la clave rechazada, ilegible…); el motivo viaja en <c>details</c>.</summary>
    public const string ConnectionUnavailable = "siigo.connection_unavailable";

    /// <summary>503: Siigo no contestó o respondió un error transitorio.</summary>
    public const string Unavailable = "siigo.unavailable";
}
