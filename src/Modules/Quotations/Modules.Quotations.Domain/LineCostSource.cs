namespace Modules.Quotations.Domain;

/// <summary>
/// De dónde salió el costo congelado en una línea del pedido (spec 2026-11-10 §4.2). Hoy sólo existe el costo
/// manual del catálogo; el enum deja lugar a otros orígenes sin migrar la columna. Es el mismo valor que el de
/// Pos, repetido porque los módulos no se referencian.
/// </summary>
public enum LineCostSource
{
    Manual,
}
