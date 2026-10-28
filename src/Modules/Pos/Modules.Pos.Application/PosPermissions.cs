namespace Modules.Pos.Application;

/// <summary>
/// Spec 2026-10-07, «Permisos y políticas». Los siete exigen la capacidad `pos` (RequiredModules; `pos.sale.customer` exige además `customers`),
/// y cada uno necesita su política en AddAuthorization: sin ella, RequireAuthorization no resuelve
/// y el síntoma es 500, no 403.
/// </summary>
public static class PosPermissions
{
    public const string SaleRead = "pos.sale.read";
    public const string SaleCreate = "pos.sale.create";

    /// <summary>
    /// Elegir o crear el cliente de una venta (spec 2026-10-14). Exige `pos` **y** `customers`: sin Clientes, el
    /// enmascarado lo quita y la caja vende a Consumidor final.
    /// </summary>
    public const string SaleCustomer = "pos.sale.customer";

    public const string SaleVoid = "pos.sale.void";

    /// <summary>Descuento de línea > 0 y venta en total 0 (spec, decisión 26).</summary>
    public const string SaleDiscount = "pos.sale.discount";

    public const string RegisterOperate = "pos.register.operate";

    /// <summary>Amplía la lectura a las cajas y ventas de otros cajeros; no la reemplaza.</summary>
    public const string RegisterRead = "pos.register.read";
}
