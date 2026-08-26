using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>
/// El estado que se muestra de una membresía, que no es el que se guarda.
/// </summary>
/// <remarks>
/// El vencimiento es perezoso: sólo <see cref="Membership.Accept"/> mueve una invitación a
/// <see cref="MembershipState.Expired"/>, y eso pasa únicamente cuando la persona intenta
/// entrar. Quien nunca lo intenta queda en <see cref="MembershipState.Invited"/> con un
/// ExpiresAt en el pasado para siempre, así que la columna cruda dice "invitada" de algo
/// que ya nadie puede usar. Este enum es esa diferencia, y existe acá —en Application, no
/// en Domain— porque es una lectura contra un reloj, no una regla de negocio: nada cambia
/// de estado al calcularlo.
/// </remarks>
public enum MembershipViewState
{
    Pending = 1,
    Expired = 2,
    Active = 3,
    Suspended = 4,
    Removed = 5
}

public static class MembershipViewStates
{
    /// <summary>
    /// Deriva el estado visible comparando la ventana de invitación contra <paramref name="now"/>.
    /// Misma regla que aplicaba el frontend antes de que el filtro viviera acá.
    /// </summary>
    public static MembershipViewState Of(MembershipState state, DateTimeOffset expiresAt, DateTimeOffset now) =>
        state switch
        {
            MembershipState.Invited => expiresAt <= now
                ? MembershipViewState.Expired
                : MembershipViewState.Pending,
            MembershipState.Expired => MembershipViewState.Expired,
            MembershipState.Active => MembershipViewState.Active,
            MembershipState.Suspended => MembershipViewState.Suspended,
            MembershipState.Removed => MembershipViewState.Removed,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown membership state.")
        };

    /// <summary>
    /// Traduce el valor crudo del query string. Un valor que no se reconoce **falla**, no se
    /// ignora: tratarlo como "sin filtro" devuelve el listado completo con un 200, y quien
    /// escribió <c>?state=vencidas</c> ve a todo el mundo y concluye que no hay ninguna
    /// vencida. Un filtro que miente en silencio es peor que un error. Mismo criterio que
    /// <c>CompanyStatusFilterParser</c>.
    /// </summary>
    public static MembershipViewState? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "pending" => MembershipViewState.Pending,
            "expired" => MembershipViewState.Expired,
            "active" => MembershipViewState.Active,
            "suspended" => MembershipViewState.Suspended,
            "removed" => MembershipViewState.Removed,
            _ => throw new TenantDomainException(
                "tenancy.membership.state_filter_invalid",
                "The state filter must be one of 'pending', 'expired', 'active', 'suspended' or 'removed'.")
        };
    }
}
