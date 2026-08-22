using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Modules.Tenancy.IntegrationTests;

// Cobertura de regresión del ítem requerido #2 del ADR 0001: el stub de auth por headers
// de desarrollo tiene que ser imposible de activar fuera del entorno Development, incluso
// con un override explícito Authentication:UseDevelopmentStub=true.
public sealed class AuthenticationStubGuardTests
{
    [Fact]
    public void DevelopmentStubCannotStartOutsideDevelopment()
    {
        using var factory = new NonDevelopmentStubFactory();

        var exception = Assert.Throws<InvalidOperationException>(() => factory.Server);
        Assert.Contains("Authentication:UseDevelopmentStub", exception.Message, StringComparison.Ordinal);
    }

    private sealed class NonDevelopmentStubFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Authentication:UseDevelopmentStub", "true");
            // No se llega a un Postgres/R2 real: la guarda tira excepción durante el registro
            // de servicios, antes de tocar la base o el cliente de storage. Estos valores sólo
            // necesitan estar presentes para que no salten primero verificaciones anteriores de
            // InvalidOperationException (cadena de conexión faltante, config de R2 faltante).
            builder.UseSetting(
                "ConnectionStrings:QepDatabase",
                "Host=localhost;Port=5432;Database=test;Username=test;Password=test");
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            // Fijado, no heredado: appsettings.json lleva el proveedor con el que se despliega el
            // producto, y una suite de integración que depende de eso termina dependiendo de las
            // credenciales de quien la corra. Con "infobip" y las claves de Infobip ausentes —CI,
            // un clon nuevo— NotificationsOptionsValidator falla al arrancar y todas las pruebas
            // del archivo mueren antes de llegar a su aserción.
            // El canal de log es el default de desarrollo (SDD-CT-03). SDD-CT-17.
            builder.UseSetting("Notifications:EmailProvider", "log");
        }
    }
}
