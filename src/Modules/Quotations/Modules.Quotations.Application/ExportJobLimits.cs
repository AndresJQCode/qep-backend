namespace Modules.Quotations.Application;

public static class ExportJobLimits
{
    /// <summary>D4: exportaciones Pending + Processing por persona en un tenant, contando
    /// cotizaciones y ventas juntas. Frena el doble clic y el abuso; no es un invariante duro
    /// —dos pedidos simultáneos pueden pasar los dos—, así que no se paga un bloqueo por él.</summary>
    public const int PendingPerRequester = 3;

    /// <summary>D8: filas por consulta al armar el Excel. No es un tope: es el lote con el que se
    /// recorre para que la memoria quede acotada a mil filas y no al año entero.</summary>
    public const int BatchSize = 1_000;
}
