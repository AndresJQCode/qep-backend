using Microsoft.Extensions.Options;
using Modules.Notifications.Application;
using Modules.Notifications.Infrastructure;
using Modules.Wholesale.Application;

namespace Bootstrapper;

/// <summary>
/// <see cref="IWholesaleInvitationLinks"/> con <see cref="AccountLinks.WholesaleInvitation"/> sobre
/// <c>Notifications:AppBaseUrl</c> (ruling W1): el link que se copia es el mismo que arma el correo de la invitación.
/// </summary>
internal sealed class WholesaleInvitationLinks(IOptions<NotificationsOptions> options) : IWholesaleInvitationLinks
{
    public string Compose(string token) => AccountLinks.WholesaleInvitation(options.Value.AppBaseUrl, token);
}
