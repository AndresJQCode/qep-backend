namespace Modules.ServiceContracts.Domain;

/// <summary>
/// Cómo se reajusta el precio de un contrato (spec reajuste §5). <c>Ipc</c> retiene el cobro de la revisión hasta que alguien
/// decide; <c>Manual</c> lo genera «sin revisar». Se guarda y viaja por nombre.
/// </summary>
public enum AdjustmentMode
{
    Manual = 1,
    Ipc = 2,
}
