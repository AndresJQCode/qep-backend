namespace Modules.Siigo.Domain;

/// <summary>
/// Qué hacía QEP cuando una llamada a Siigo falló (spec 2026-11-21, fallas de las llamadas a Siigo). Se guarda por nombre en
/// <c>siigo.call_failures.operation</c> y es contrato con la pantalla: no se renombra.
/// </summary>
public enum SiigoCallOperation
{
    /// <summary>La autenticación (<c>POST /auth</c>), sea cual sea la operación que la pidió.</summary>
    Auth,

    /// <summary>Una lista de opciones (comprobantes, usuarios, bodegas, centros de costo, formas de pago, impuestos).</summary>
    ListRead,

    ProductSearch,

    CustomerFind,

    CustomerCreate,

    InvoiceCreate,

    InvoiceStamp,

    StampErrors,

    /// <summary>La descarga del PDF de una factura ya creada.</summary>
    InvoicePdf,

    /// <summary>La lectura de una factura ya creada para verificar que se anuló en Siigo (spec 2026-11-25, anulación §2).</summary>
    InvoiceRead,
}
