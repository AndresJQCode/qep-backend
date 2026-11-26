namespace Modules.Wms.Domain.Warehousing;

/// <summary><c>Self</c> es el propio tenant (uno por tenant); <c>Client</c>, un tercero dueño de mercancía en custodia.</summary>
public enum OwnerKind
{
    Self,
    Client,
}
