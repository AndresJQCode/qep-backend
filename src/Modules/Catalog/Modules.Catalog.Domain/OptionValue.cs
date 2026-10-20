namespace Modules.Catalog.Domain;

/// <summary>
/// Un valor de una opción de la biblioteca del tenant (por ejemplo «M» dentro de «Talla»).
/// Se crea y se edita sólo desde <see cref="OptionDefinition"/>, que hace cumplir la unicidad.
/// </summary>
public sealed class OptionValue
{
    // EF Core materializa por acá.
    private OptionValue() => Value = string.Empty;

    internal OptionValue(Guid id, string value, int position)
    {
        Id = id;
        Value = value;
        Position = position;
        IsActive = true;
    }

    public Guid Id { get; private set; }

    public string Value { get; internal set; }

    /// <summary>Posición base 0 dentro de la opción; define el orden en que se muestran.</summary>
    public int Position { get; internal set; }

    public bool IsActive { get; internal set; }
}
