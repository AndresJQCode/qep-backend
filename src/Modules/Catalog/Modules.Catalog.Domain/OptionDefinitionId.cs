namespace Modules.Catalog.Domain;

public readonly record struct OptionDefinitionId(Guid Value)
{
    public static OptionDefinitionId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
