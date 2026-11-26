namespace Modules.Store.Application;

/// <summary>El backend no tiene catálogo de países: «uno real» es una región que conoce el normalizador de teléfonos (hecho verificado 17).</summary>
public interface IStoreCountries
{
    bool IsKnown(string country);
}
