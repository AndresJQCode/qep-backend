namespace Modules.Customers.Domain;

/// <summary>
/// Spec 2026-10-10 §6.2: si la ficha tiene todo lo que hace falta para cotizarle y venderle. Un cliente
/// que nace de un mensaje de WhatsApp es <see cref="Incomplete"/>: sin CUC, documento, dirección,
/// ciudad ni clasificación. Completar es el <c>PUT</c> de siempre con todos los datos. No se llama
/// «estado» porque Customers ya tiene uno (<see cref="Customer.IsActive"/>).
/// </summary>
public enum CustomerCompleteness
{
    Complete,
    Incomplete,
}
