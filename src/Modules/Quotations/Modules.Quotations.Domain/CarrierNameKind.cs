namespace Modules.Quotations.Domain;

/// <summary>Name es el nombre que ve la persona; External, un alias con el que otro sistema
/// (por ejemplo Shopify) llama a la misma transportadora.</summary>
public enum CarrierNameKind
{
    Name,
    External,
}
