namespace Modules.ServiceContracts.Domain;

/// <summary>Quién genera (spec reajuste §6.3).</summary>
public enum GenerationMode
{
    /// <summary>Crear, importar y la vista previa: nunca retiene ni marca «sin revisar», porque el precio escrito es el de hoy.</summary>
    Creation = 1,

    /// <summary>La corrida diaria y las acciones que liberan una revisión.</summary>
    Run = 2,
}
