namespace Modules.Store.Domain;

/// <summary>El orden del enum es el del contrato: el primero que falle da el código del 422.</summary>
public enum StorePublishBlocker
{
    SlugRequired,
    CurrencyRequired,
    SalesCountryRequired,
}
