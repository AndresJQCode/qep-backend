namespace Modules.Catalog.Domain;

/// <summary>
/// Spec 2026-11-22 (D-4): los canales que se asignan a una lista en «Precios por canal». Viaja y se guarda por nombre. Los
/// distribuidores no están acá: cada uno tiene su propia lista en Mayorista.
/// </summary>
public enum PriceChannel
{
    Pos,
    Store,
    Quotations,
}
