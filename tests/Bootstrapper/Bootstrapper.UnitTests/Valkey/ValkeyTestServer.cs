using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Bootstrapper.UnitTests.Valkey;

/// <summary>
/// Un Valkey de prueba en puerto aleatorio. Con <c>withProductionAcl</c> carga la misma ACL que el usuario <c>qep</c> tiene
/// en producción (DP-V4 del spec de Valkey). Usa el <c>ContainerBuilder</c> genérico y no <c>Testcontainers.Redis</c>
/// (V2 del plan): la espera de <c>RedisBuilder</c> es un <c>PING</c> sin usuario, que con <c>user default off</c> nunca
/// responde. El Valkey local del developer (<c>localhost:6379</c>) no se toca.
/// </summary>
internal static class ValkeyTestServer
{
    internal const string Image = "valkey/valkey:9.1.3-alpine";

    // Valor de prueba, no es un secreto: sólo existe dentro del contenedor efímero.
    internal const string QepPassword = "qep-acl-test";

    // Sólo para leer ACL LOG en las pruebas; no existe en producción y no cambia la línea de qep.
    internal const string AuditorPassword = "auditor-acl-test";

    private const int Port = 6379;
    private const string AclPath = "/etc/valkey/users.acl";

    private static readonly string Acl =
        "user default off\n" +
        $"user qep on >{QepPassword} ~qep:* &qep:* +@all -@dangerous -@scripting +info\n" +
        $"user auditor on >{AuditorPassword} ~* &* +@all\n";

    internal static async Task<IContainer> StartAsync(bool withProductionAcl, CancellationToken ct)
    {
        var builder = new ContainerBuilder(Image)
            .WithPortBinding(Port, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections"));

        if (withProductionAcl)
        {
            builder = builder
                .WithResourceMapping(Encoding.UTF8.GetBytes(Acl), AclPath)
                .WithCommand("valkey-server", "--aclfile", AclPath);
        }

        var container = builder.Build();
        await container.StartAsync(ct);
        return container;
    }

    internal static string AuditorConnectionString(IContainer container) =>
        $"{container.Hostname}:{container.GetMappedPublicPort(Port)},user=auditor,password={AuditorPassword},abortConnect=false";

    internal static string ConnectionString(IContainer container, bool asQep) =>
        $"{container.Hostname}:{container.GetMappedPublicPort(Port)}" +
        (asQep ? $",user=qep,password={QepPassword}" : string.Empty) +
        ",abortConnect=false";
}
