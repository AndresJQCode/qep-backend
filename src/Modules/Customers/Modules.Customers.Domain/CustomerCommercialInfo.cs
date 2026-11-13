namespace Modules.Customers.Domain;

/// <summary>
/// Los datos comerciales del cliente: como esta clasificado y si se le aplica retencion.
///
/// Van juntos por la misma razon que los de contacto, y ademas porque cambian por la misma razon:
/// los dos los define el area comercial, no quien carga el alta.
/// </summary>
public sealed record CustomerCommercialInfo
{
    /// <summary>
    /// FK opcional a <see cref="ClientClassification"/>. Reemplaza al enum fijo
    /// <c>CustomerClassification</c> (Pequeno/Mediano/Grande): ese catalogo no tenia ninguna
    /// relacion con este agregado y ya no tiene consumidores.
    ///
    /// <c>null</c> es un cliente sin clasificacion, y su CUC no lleva prefijo (decision del owner,
    /// 2026-10-19): solo departamento y consecutivo, los ocho caracteres estables. Asignarle una
    /// despues antepone el prefijo; quitarla lo vuelve a sacar.
    /// </summary>
    public ClientClassificationId? ClassificationId { get; init; }

    /// <summary>
    /// Si el cliente tiene excedente de IVA.
    ///
    /// <c>bool</c> y no <c>bool?</c>: el formulario lo presenta como un interruptor con dos
    /// estados, sin "sin definir". Las retenciones ya no viajan acá: son tasas asignadas
    /// (<see cref="Customer.SetWithholdingTaxRates"/>).
    /// </summary>
    public bool VatSurplus { get; init; }

    /// <summary>
    /// Sin clasificacion y sin excedente de IVA. Existe para pruebas que arman un
    /// <c>CustomerCommercialInfo</c> parcial y sobreescriben el campo que les importa con un
    /// <c>with</c> o un inicializador propio.
    /// </summary>
    public static CustomerCommercialInfo Empty { get; } = new();
}
