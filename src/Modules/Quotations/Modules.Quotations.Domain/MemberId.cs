namespace Modules.Quotations.Domain;

/// <summary>
/// Referencia a un <c>Membership</c> del módulo Tenancy (documento §1.4: "asesora"/"usuario"
/// siempre refieren a <c>members</c>, no a <c>identity.users</c>). Tipado propio y no el
/// <c>MembershipId</c> de Tenancy: el dominio de un módulo de negocio no referencia el dominio de
/// otro (mismo criterio que <c>Customer.CityId</c>, que es un <see cref="Guid"/> suelto hacia
/// Geography).
/// </summary>
public readonly record struct MemberId(Guid Value)
{
    public override string ToString() => Value.ToString();
}
