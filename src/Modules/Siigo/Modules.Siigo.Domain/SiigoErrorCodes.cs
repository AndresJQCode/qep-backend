namespace Modules.Siigo.Domain;

/// <summary>Los códigos de error de Siigo (<c>siigo.*</c>). Son contrato con el frontend, que traduce por ellos: no se renombran. El
/// status lo decide el mapeo central (<c>ApiExceptionHandler</c>), nunca un handler.</summary>
public static class SiigoErrorCodes
{
    /// <summary>409: otra conexión del tenant ya factura con esa empresa (<c>IX_settings_tenant_company</c>).</summary>
    public const string SettingsCompanyTaken = "siigo.settings.company_taken";

    /// <summary>422: la empresa no existe en el tenant o está inactiva.</summary>
    public const string SettingsCompanyInvalid = "siigo.settings.company_invalid";

    /// <summary>422: un vendedor se vincula a una membresía que no es del tenant ni tenía vínculo (spec 2026-11-20).</summary>
    public const string SettingsMemberNotFound = "siigo.settings.member_not_found";

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

    /// <summary>422: el pedido no está listo para facturar; <c>details.missing</c> trae todo lo que falta (spec §5.4, paso 2).</summary>
    public const string InvoiceNotReady = "siigo.invoice.not_ready";

    /// <summary>422: los ítems no cuadran con el total del pedido (más de un peso), o Siigo rechazó los pagos por no sumar su total
    /// (spec §6.4).</summary>
    public const string InvoiceTotalsMismatch = "siigo.invoice.totals_mismatch";

    /// <summary>422: los comprobantes de pago suman más que el neto del pedido.</summary>
    public const string InvoicePaymentsExceedTotal = "siigo.invoice.payments_exceed_total";

    /// <summary>422: el cuerpo nombra un comprobante que no es del pedido.</summary>
    public const string InvoicePaymentsMismatch = "siigo.invoice.payments_mismatch";

    /// <summary>422: sólo se factura en pesos colombianos.</summary>
    public const string InvoiceCurrencyUnsupported = "siigo.invoice.currency_unsupported";

    /// <summary>422: la cuenta de cobro del pedido no tiene empresa.</summary>
    public const string InvoiceNoCompany = "siigo.invoice.no_company";

    /// <summary>422: la empresa del pedido no tiene una conexión de Siigo configurada.</summary>
    public const string InvoiceNoConnection = "siigo.invoice.no_connection";

    /// <summary>409: el índice único parcial de <c>siigo.invoices</c> (una factura vigente por pedido y tipo).</summary>
    public const string InvoiceAlreadyExists = "siigo.invoice.already_exists";

    /// <summary>422: la factura no está en el estado que la acción exige (resolver sólo desde <c>Unknown</c> o una <c>Pending</c> que no se
    /// esté mandando, estampar sólo una creada).</summary>
    public const string InvoiceStateInvalid = "siigo.invoice.state_invalid";

    /// <summary>422: se declaró que la factura no existe antes de tiempo; <c>details.availableAt</c> dice desde cuándo se puede.</summary>
    public const string InvoiceResolveTooEarly = "siigo.invoice.resolve_too_early";

    /// <summary>404: el pedido no tiene factura de Siigo de ese tipo.</summary>
    public const string InvoiceNotFound = "siigo.invoice.not_found";

    /// <summary>422: una factura vigente del pedido no se puede verificar en Siigo (está por confirmar, es <c>Unknown</c> o se resolvió a
    /// mano sin id); <c>details = { kind, number }</c>.</summary>
    public const string InvoiceAnnulmentUnverifiable = "siigo.invoice.annulment_unverifiable";

    /// <summary>422: Siigo dice que la factura no está anulada; <c>details = { kind, number }</c>.</summary>
    public const string InvoiceNotAnnulled = "siigo.invoice.not_annulled";

    /// <summary>422: la respuesta de Siigo no se reconoce o es contradictoria, así que no se libera nada;
    /// <c>details = { kind, number }</c>.</summary>
    public const string InvoiceAnnulmentUnclear = "siigo.invoice.annulment_unclear";

    // El motivo por el que una factura quedó Annulled: no son respuestas HTTP, viajan en la fila (`error_code`) y en el ítem de la tanda.

    /// <summary>Una persona la anuló en Siigo y QEP lo verificó.</summary>
    public const string InvoiceAnnulledInSiigo = "siigo.invoice.annulled_in_siigo";

    /// <summary>Una persona la borró en Siigo y QEP lo verificó.</summary>
    public const string InvoiceDeletedInSiigo = "siigo.invoice.deleted_in_siigo";

    // El motivo por el que una factura sigue Pending (spec 2026-11-21 siigo-motivo-pendiente): no son respuestas HTTP, viajan en la fila
    // (`error_code`) y la pantalla las traduce.

    /// <summary>Siigo no contestó a tiempo.</summary>
    public const string InvoiceTimeout = "siigo.invoice.timeout";

    /// <summary>No hubo conexión con Siigo.</summary>
    public const string InvoiceNetwork = "siigo.invoice.network";

    /// <summary>Siigo contestó 429: pidió esperar.</summary>
    public const string InvoiceRateLimited = "siigo.invoice.rate_limited";

    /// <summary>Siigo falló (5xx) o no se pudo llamar por algo que el tenant no puede corregir (configuración del servidor).</summary>
    public const string InvoiceUnavailable = "siigo.invoice.unavailable";

    /// <summary>Siigo contestó un error de servicio (<c>documents_service</c>, <c>general_service</c>…): la factura no salió por una caída de
    /// Siigo, no por la plataforma ni por los datos del pedido. Reintenta en unos minutos.</summary>
    public const string InvoiceSiigoService = "siigo.invoice.siigo_service";

    /// <summary>El envío se cortó sin registrar resultado (el proceso murió o se redesplegó): no se sabe si la factura salió.</summary>
    public const string InvoiceInterrupted = "siigo.invoice.interrupted";

    /// <summary>La factura del pedido existe en Siigo y la de flete sigue <c>Pending</c>, haya salido o no. Sólo viaja en un ítem de
    /// tanda: la pantalla avisa que la del pedido no se vuelve a crear y que falta la de flete.</summary>
    public const string InvoiceFreightPending = "siigo.invoice.freight_pending";

    /// <summary>La factura del pedido existe en Siigo y la de flete no se creó (Siigo la rechazó o alguien declaró que no existe): QEP
    /// no la vuelve a armar. Sólo viaja en un ítem de tanda.</summary>
    public const string InvoiceFreightNotCreated = "siigo.invoice.freight_not_created";

    /// <summary>La factura del pedido existe en Siigo y no se sabe si la de flete también (<c>Unknown</c>): la resuelve una persona desde
    /// el pedido. Sólo viaja en un ítem de tanda.</summary>
    public const string InvoiceFreightUnconfirmed = "siigo.invoice.freight_unconfirmed";

    /// <summary>Una persona declaró que la factura por confirmar no existe en Siigo (lo dice la tanda que la había dejado así).</summary>
    public const string InvoiceResolvedMissing = "siigo.invoice.resolved_missing";

    /// <summary>404: la falla de llamada a Siigo que se pide no existe en el tenant (o ya expiró a los 7 días).</summary>
    public const string CallFailureNotFound = "siigo.call_failure.not_found";

    /// <summary>422: una tanda de facturación lleva entre 1 y 50 pedidos.</summary>
    public const string InvoiceBatchSize = "siigo.invoice_batch.size";

    /// <summary>422: los pedidos de una tanda son obligatorios y no se repiten.</summary>
    public const string InvoiceBatchOrders = "siigo.invoice_batch.orders";

    /// <summary>404: la tanda no existe en el tenant.</summary>
    public const string InvoiceBatchNotFound = "siigo.invoice_batch.not_found";

    /// <summary>El pedido de una tanda no se facturó porque quien la pidió ya no tiene permiso o membresía al procesarlo.</summary>
    public const string InvoiceBatchForbidden = "siigo.invoice_batch.forbidden";

    /// <summary>409: un pedido ya está en una tanda sin terminar; <c>details.orderIds</c> dice cuáles.</summary>
    public const string InvoiceBatchOrderInProgress = "siigo.invoice_batch.order_in_progress";
}
