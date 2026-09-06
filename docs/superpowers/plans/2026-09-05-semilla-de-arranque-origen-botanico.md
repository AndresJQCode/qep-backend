# Semilla de arranque "Origen botánico" — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que un arranque de la aplicación con `Seed:Enabled` deje el ambiente desplegado utilizable —tenant, usuario, membresía admin y los 19 productos— sin ningún paso manual, y que borrar la base y volver a desplegar lo reconstruya solo.

**Architecture:** Cuatro seeders, uno por módulo, cada uno tocando **sólo las tablas de su módulo**, expuestos como métodos de extensión sobre `IServiceProvider` igual que los `Initialize*DatabaseAsync` que ya existen. Un orquestador delgado en `Bootstrapper` lee las opciones, encadena los cuatro y hace pasar el id de usuario entre Identity y Tenancy; `Program.cs` lo llama en una línea, después de la cadena de migraciones. El id del tenant es una constante en código, así que Catalog nunca le pregunta a Tenancy quién es.

**Tech Stack:** .NET 10, EF Core + Npgsql, `Microsoft.Extensions.Options` con `ValidateOnStart`, `System.Text.Json`, xUnit v3 + Testcontainers.

**Spec:** [`docs/superpowers/specs/2026-09-05-semilla-de-arranque-origen-botanico-design.md`](../specs/2026-09-05-semilla-de-arranque-origen-botanico-design.md)

## Global Constraints

- **TDD obligatorio.** RED antes que GREEN, con evidencia literal de ambos. Sin excepción.
- **Conventional commits, sin atribución de IA.**
- **Antes de commitear se comprueba la rama con `git branch --show-current` en ese momento.** Si es `main`, se crea una rama primero.
- **No inventar.** Campos, estados, rutas, permisos, roles y códigos de error tienen que existir en el código.
- **Detener `Api.exe` / `dotnet run` antes de compilar o testear:** un proceso vivo bloquea el archivo y `dotnet build`, `dotnet test` y los comandos `ef` fallan.
- **Toda factory de integración fija `Notifications:EmailProvider` en `"log"`.** Sin eso hereda `"infobip"` de `appsettings.json`, `NotificationsOptionsValidator` falla al arrancar y **todas** las pruebas del archivo mueren antes de su aserción.
- **Docker tiene que estar corriendo:** las pruebas de integración levantan PostgreSQL con Testcontainers.
- Nombre del tenant: `Origen botánico`. Slug: `origen-botanico`. Email admin: se declara por configuración, **nunca hardcodeado**.
- Comandos de verificación: `dotnet build`, `dotnet test`. Los del developer van en PowerShell.

## Estructura de archivos

| Archivo | Responsabilidad |
| --- | --- |
| `src/Bootstrapper/Seeding/SeedOptions.cs` | Las dos claves de configuración |
| `src/Bootstrapper/Seeding/SeedOptionsValidator.cs` | Aborta el arranque si está prendida sin email |
| `src/Bootstrapper/Seeding/QepSeedRunner.cs` | Orquesta los cuatro en orden y emite la advertencia |
| `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Seed/TenancySeeder.cs` | Tenant con id constante + membresía del owner |
| `src/Modules/Identity/Modules.Identity.Infrastructure/Seed/IdentitySeeder.cs` | Usuario por email |
| `src/Modules/Catalog/Modules.Catalog.Infrastructure/Seed/CatalogSeeder.cs` | Tasa + productos |
| `src/Modules/Catalog/Modules.Catalog.Infrastructure/Seed/CatalogSeedFile.cs` | Modelo del JSON |
| `src/Modules/Catalog/Modules.Catalog.Infrastructure/Seed/Data/catalog-products.json` | Los datos, recurso embebido |
| `src/Api/Program.cs` | Una línea que llama al orquestador |
| `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/SeedStartupTests.cs` | Interruptor, tenant, usuario, membresía |
| `tests/Modules/Catalog/Modules.Catalog.UnitTests/CatalogSeedFileTests.cs` | El parser del recurso embebido |
| `tests/Modules/Catalog/Modules.Catalog.IntegrationTests/CatalogSeedTests.cs` | Catálogo sembrado e idempotencia |

Cada seeder vive en su propio módulo porque **ningún módulo lee las tablas de otro**, y `ArchitectureTests` lo verifica. El único lugar que conoce a los cuatro es `Bootstrapper`, que es el composition root y ya referencia a todos.

---

### Task 1: Opciones, validador y orquestador vacío

Deja el interruptor funcionando de punta a punta antes de que exista ningún seeder: apagado no pasa nada, prendido sin email la aplicación no arranca. Las tareas 2 a 5 sólo agregan pasos adentro.

**Files:**
- Create: `src/Bootstrapper/Seeding/SeedOptions.cs`
- Create: `src/Bootstrapper/Seeding/SeedOptionsValidator.cs`
- Create: `src/Bootstrapper/Seeding/QepSeedRunner.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (registrar las opciones)
- Modify: `src/Api/Program.cs` (llamar al orquestador después de `InitializeQuotationsDatabaseAsync`)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/SeedStartupTests.cs`

**Interfaces:**
- Produces: `SeedOptions` con `SectionName = "Seed"`, `bool Enabled`, `string? OwnerEmail`.
- Produces: `IServiceProvider.RunQepSeedAsync(CancellationToken)` — no hace nada si `Enabled` es `false`.

- [ ] **Step 1: Escribir las dos pruebas que fallan**

En `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/SeedStartupTests.cs`:

```csharp
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

public sealed class SeedStartupTests
{
    // La semilla crea un tenant y otorga admin. Que esté apagada por defecto es la única
    // defensa que tiene el ambiente desplegado, así que se prueba explícitamente.
    [Fact]
    public async Task SeedDisabledCreatesNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), seedEnabled: false);
        using var client = factory.CreateClient();

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var seeded = await dbContext.Tenants
            .AnyAsync(tenant => tenant.Slug == "origen-botanico", TestContext.Current.CancellationToken);

        Assert.False(seeded);
    }

    // Prendida sin email no se puede sembrar la membresía, y un tenant al que nadie puede
    // entrar es peor que no sembrar nada. Mismo criterio que la cadena de conexión: fallar
    // con un mensaje que dice qué falta.
    [Fact]
    public async Task SeedEnabledWithoutOwnerEmailFailsStartup()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(
            database.GetConnectionString(), seedEnabled: true, ownerEmail: string.Empty);

        // ThrowsAny y no Throws<OptionsValidationException>: ValidateOnStart lanza durante
        // host.StartAsync(), y WebApplicationFactory puede entregarla envuelta. Lo que se afirma
        // es que el arranque muere y que el mensaje nombra la clave que falta, no el tipo exacto.
        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var messages = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        Assert.Contains(messages, message => message.Contains("Seed:OwnerEmail", StringComparison.Ordinal));
    }

    private static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }

    private sealed class QepApiFactory(
        string connectionString,
        bool seedEnabled,
        string? ownerEmail = "semilla@qcode.co")
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            // Fijado, nunca heredado: con "infobip" y sus claves ausentes el validador de
            // Notifications falla al arrancar y todas las pruebas del archivo mueren antes
            // de su aserción.
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Seed:Enabled", seedEnabled ? "true" : "false");
            builder.UseSetting("Seed:OwnerEmail", ownerEmail ?? string.Empty);
        }
    }
}
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~SeedStartupTests"
```

Esperado: **FAIL**. `SeedDisabledCreatesNothing` no compila o pasa por accidente; `SeedEnabledWithoutOwnerEmailFailsStartup` falla porque no se lanza ninguna `OptionsValidationException` — nadie valida la sección `Seed` todavía. Copiar la salida literal.

- [ ] **Step 3: Crear `SeedOptions`**

```csharp
namespace Bootstrapper.Seeding;

/// <summary>
/// La semilla de arranque del ambiente desplegado. No es sólo cargar datos: crea un tenant y
/// le otorga el rol admin a un email, así que es un mecanismo que concede privilegios.
///
/// El truco fail-closed del stub de auth —abortar si lo prenden fuera de Development— acá no
/// sirve: el ambiente objetivo corre con ASPNETCORE_ENVIRONMENT=Production. La defensa es que
/// <see cref="Enabled"/> nace apagado y que prenderlo exige declarar a quién se le da admin.
/// </summary>
public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    public bool Enabled { get; set; }

    /// <summary>
    /// Email que recibe la membresía admin. Sin valor por defecto y sin hardcodear: dejar una
    /// persona fija en un archivo versionado haría del repositorio la autoridad sobre quién
    /// administra un tenant, y eso es una decisión del ambiente.
    /// </summary>
    public string? OwnerEmail { get; set; }
}
```

- [ ] **Step 4: Crear `SeedOptionsValidator`**

```csharp
using Microsoft.Extensions.Options;
using Modules.Identity.Domain;

namespace Bootstrapper.Seeding;

// Falla rápido al arrancar (ValidateOnStart), igual que AuditOptionsValidator y compañía:
// una semilla prendida sin email sembraría un tenant al que nadie puede entrar, y eso se
// descubriría recién al intentar usarlo.
internal sealed class SeedOptionsValidator : IValidateOptions<SeedOptions>
{
    public ValidateOptionsResult Validate(string? name, SeedOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        if (string.IsNullOrWhiteSpace(options.OwnerEmail))
        {
            return ValidateOptionsResult.Fail(
                "Seed:OwnerEmail is required when Seed:Enabled is true.");
        }

        // Se normaliza con la misma regla del dominio de Identity, no con una propia: si el
        // email no pasa acá, tampoco va a pasar cuando el seeder cree el usuario, y el
        // arranque es mejor lugar para enterarse que el medio de la siembra.
        try
        {
            User.NormalizeEmail(options.OwnerEmail);
        }
        catch (IdentityDomainException)
        {
            return ValidateOptionsResult.Fail(
                $"Seed:OwnerEmail '{options.OwnerEmail}' is not a valid email address.");
        }

        return ValidateOptionsResult.Success;
    }
}
```

- [ ] **Step 5: Crear `QepSeedRunner` (todavía sin sembrar nada)**

```csharp
using Bootstrapper.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bootstrapper.Seeding;

public static class QepSeedRunner
{
    private static readonly Action<ILogger, string, string, Exception?> LogSeedEnabled =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(4100, nameof(LogSeedEnabled)),
            "Seeding is ENABLED. Creating tenant '{TenantSlug}' and granting the admin role "
            + "to '{OwnerEmail}'. Disable Seed:Enabled before handing this environment over.");

    /// <summary>
    /// Corre la semilla del ambiente desplegado. No hace nada si <c>Seed:Enabled</c> está
    /// apagado. Es idempotente: lo que ya existe se saltea.
    /// </summary>
    public static async Task RunQepSeedAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<SeedOptions>>().Value;
        if (!options.Enabled)
        {
            return;
        }

        // Ruidoso a propósito: si el ambiente pasa al cliente con la clave prendida, tiene que
        // verse en los logs del primer arranque y no seis meses después.
        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(QepSeedRunner).FullName!);
        LogSeedEnabled(logger, "origen-botanico", options.OwnerEmail!, null);

        // Las tareas 2 a 5 agregan los pasos acá.
    }
}
```

- [ ] **Step 6: Registrar las opciones**

En `src/Bootstrapper/QepServiceCollectionExtensions.cs`, dentro del mismo método donde ya se registran las demás opciones, agregar:

```csharp
services.AddOptions<SeedOptions>()
    .Bind(configuration.GetSection(SeedOptions.SectionName))
    .ValidateOnStart();
services.AddSingleton<IValidateOptions<SeedOptions>, SeedOptionsValidator>();
```

- [ ] **Step 7: Llamar al orquestador desde `Program.cs`**

Inmediatamente después de `await app.Services.InitializeQuotationsDatabaseAsync(...)` y **antes** de `await app.RunAsync()`:

```csharp
// Después de todas las migraciones: la semilla escribe en las tablas de cuatro módulos y
// necesita que existan. Apagada por defecto — ver SeedOptions.
await app.Services.RunQepSeedAsync(app.Lifetime.ApplicationStopping);
```

- [ ] **Step 8: Correr y verificar que pasan**

```powershell
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~SeedStartupTests"
```

Esperado: **PASS**, 2 de 2. Copiar la salida literal.

- [ ] **Step 9: Commit**

```bash
git branch --show-current   # si dice main, crear rama antes
git add src/Bootstrapper/Seeding src/Bootstrapper/QepServiceCollectionExtensions.cs src/Api/Program.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/SeedStartupTests.cs
git commit -m "feat(seed): agregar el interruptor de la semilla de arranque"
```

---

### Task 2: Seeder del tenant

**Files:**
- Create: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Seed/TenancySeeder.cs`
- Modify: `src/Bootstrapper/Seeding/QepSeedRunner.cs`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/SeedStartupTests.cs`

**Interfaces:**
- Consumes: `SeedOptions`, `RunQepSeedAsync` de la Task 1.
- Produces: `TenancySeeder.SeedTenantId` (`Guid`), `TenancySeeder.SeedTenantSlug` (`string`), `IServiceProvider.SeedTenantAsync(CancellationToken)`.

- [ ] **Step 1: Escribir la prueba que falla**

Agregar a `SeedStartupTests.cs`:

```csharp
    [Fact]
    public async Task SeedCreatesTheTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), seedEnabled: true);
        using var client = factory.CreateClient();

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var tenant = await dbContext.Tenants.SingleOrDefaultAsync(
            candidate => candidate.Id == new TenantId(TenancySeeder.SeedTenantId),
            TestContext.Current.CancellationToken);

        Assert.NotNull(tenant);
        Assert.Equal("origen-botanico", tenant.Slug);
        Assert.Equal("Origen botánico", tenant.DisplayName);
    }
```

- [ ] **Step 2: Correr y verificar que falla**

```powershell
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~SeedCreatesTheTenant"
```

Esperado: **FAIL** de compilación — `TenancySeeder` no existe. Copiar la salida.

- [ ] **Step 3: Crear `TenancySeeder` con el tenant**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;

namespace Modules.Tenancy.Infrastructure.Seed;

/// <summary>
/// La mitad de Tenancy de la semilla de arranque. Siembra sólo tablas de este módulo.
/// </summary>
public static class TenancySeeder
{
    /// <summary>
    /// Constante y no configuración: es lo que hace que el id sobreviva a borrar la base, y que
    /// Catalog no tenga que preguntarle a Tenancy quién es el tenant. Se elige `...0003` para no
    /// chocar con DevelopmentTenantId (`...0001`) ni con el sujeto de desarrollo (`...0002`).
    /// </summary>
    public static readonly Guid SeedTenantId =
        Guid.Parse("01900000-0000-7000-8000-000000000003");

    public const string SeedTenantSlug = "origen-botanico";
    public const string SeedTenantDisplayName = "Origen botánico";

    public static async Task SeedTenantAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        var tenantId = new TenantId(SeedTenantId);
        if (await dbContext.Tenants.AnyAsync(
            tenant => tenant.Id == tenantId, cancellationToken))
        {
            return;
        }

        dbContext.Tenants.Add(Tenant.Create(
            tenantId,
            SeedTenantSlug,
            SeedTenantDisplayName,
            "es-CO",
            "America/Bogota",
            "yyyy-MM-dd",
            DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
```

- [ ] **Step 4: Llamarlo desde el orquestador**

En `QepSeedRunner.RunQepSeedAsync`, reemplazar el comentario `// Las tareas 2 a 5 ...` por:

```csharp
        await services.SeedTenantAsync(cancellationToken);
```

y agregar `using Modules.Tenancy.Infrastructure.Seed;` arriba.

- [ ] **Step 5: Correr y verificar que pasa**

```powershell
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~SeedStartupTests"
```

Esperado: **PASS**, 3 de 3.

- [ ] **Step 6: Commit**

```bash
git add src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Seed src/Bootstrapper/Seeding/QepSeedRunner.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/SeedStartupTests.cs
git commit -m "feat(tenancy): sembrar el tenant Origen botanico con id constante"
```

---

### Task 3: Seeder del usuario

**Files:**
- Create: `src/Modules/Identity/Modules.Identity.Infrastructure/Seed/IdentitySeeder.cs`
- Modify: `src/Bootstrapper/Seeding/QepSeedRunner.cs`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/SeedStartupTests.cs`

**Interfaces:**
- Produces: `IServiceProvider.SeedUserAsync(string email, CancellationToken)` que **devuelve `Guid`**, el id del usuario. La Task 4 lo consume.

- [ ] **Step 1: Escribir la prueba que falla**

```csharp
    // El usuario nace sin proveedor vinculado a propósito: ProviderLinkingService lo vincula
    // solo en el primer login con Google, buscándolo por email verificado. Sembrarlo Invited
    // es exactamente lo que esa ruta espera encontrar.
    [Fact]
    public async Task SeedCreatesTheOwnerUser()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(
            database.GetConnectionString(), seedEnabled: true, ownerEmail: "Semilla@QCode.CO");
        using var client = factory.CreateClient();

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var user = await dbContext.Users.SingleOrDefaultAsync(
            candidate => candidate.Email == "semilla@qcode.co",
            TestContext.Current.CancellationToken);

        Assert.NotNull(user);
    }
```

Agregar `using Modules.Identity.Infrastructure.Persistence;` al archivo.

- [ ] **Step 2: Correr y verificar que falla**

```powershell
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~SeedCreatesTheOwnerUser"
```

Esperado: **FAIL** — el usuario no existe.

- [ ] **Step 3: Crear `IdentitySeeder`**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Identity.Domain;
using Modules.Identity.Infrastructure.Persistence;

namespace Modules.Identity.Infrastructure.Seed;

/// <summary>
/// La mitad de Identity de la semilla de arranque.
/// </summary>
public static class IdentitySeeder
{
    /// <summary>
    /// Crea el usuario por email si no existe y devuelve su id. **No vincula ningún proveedor**:
    /// el `sub` de Google no se conoce hasta el primer login, y no hace falta —
    /// <c>ProviderLinkingService.LinkAndActivateAsync</c> busca por email verificado y lo
    /// vincula ahí. Un usuario sembrado sin proveedor es justo lo que esa ruta espera.
    /// </summary>
    public static async Task<Guid> SeedUserAsync(
        this IServiceProvider services,
        string email,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        var normalizedEmail = User.NormalizeEmail(email);
        var existing = await dbContext.Users.SingleOrDefaultAsync(
            user => user.Email == normalizedEmail, cancellationToken);
        if (existing is not null)
        {
            return existing.Id.Value;
        }

        var created = User.CreateInvited(UserId.New(), normalizedEmail, DateTimeOffset.UtcNow);
        dbContext.Users.Add(created);
        await dbContext.SaveChangesAsync(cancellationToken);
        return created.Id.Value;
    }
}
```

- [ ] **Step 4: Llamarlo desde el orquestador**

En `RunQepSeedAsync`, después de `SeedTenantAsync`:

```csharp
        var ownerUserId = await services.SeedUserAsync(options.OwnerEmail!, cancellationToken);
```

y agregar `using Modules.Identity.Infrastructure.Seed;`.

- [ ] **Step 5: Correr y verificar que pasa**

```powershell
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~SeedStartupTests"
```

Esperado: **PASS**, 4 de 4.

- [ ] **Step 6: Commit**

```bash
git add src/Modules/Identity/Modules.Identity.Infrastructure/Seed src/Bootstrapper/Seeding/QepSeedRunner.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/SeedStartupTests.cs
git commit -m "feat(identity): sembrar el usuario owner por email"
```

---

### Task 4: Seeder de la membresía

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Seed/TenancySeeder.cs`
- Modify: `src/Bootstrapper/Seeding/QepSeedRunner.cs`
- Test: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/SeedStartupTests.cs`

**Interfaces:**
- Consumes: el `Guid` que devuelve `SeedUserAsync`.
- Produces: `IServiceProvider.SeedOwnerMembershipAsync(Guid userId, CancellationToken)`.

- [ ] **Step 1: Escribir la prueba que falla**

```csharp
    // Sin membresía activa el tenant es invisible: ExternalClaimsTransformation resuelve los
    // permisos desde la membresía, así que un tenant sembrado sin ella devuelve 403 en todo.
    [Fact]
    public async Task SeedCreatesAnActiveAdminMembership()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), seedEnabled: true);
        using var client = factory.CreateClient();

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var membership = await dbContext.Memberships.SingleOrDefaultAsync(
            candidate => candidate.TenantId == new TenantId(TenancySeeder.SeedTenantId),
            TestContext.Current.CancellationToken);

        Assert.NotNull(membership);
        Assert.Equal(MembershipState.Active, membership.State);
        Assert.Contains("admin", membership.Roles);
    }
```

- [ ] **Step 2: Correr y verificar que falla**

```powershell
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~SeedCreatesAnActiveAdminMembership"
```

Esperado: **FAIL** — no hay membresía.

- [ ] **Step 3: Agregar el método a `TenancySeeder`**

```csharp
    /// <summary>
    /// Crea la membresía del owner, ya en <c>Active</c>. Usa
    /// <see cref="Membership.RegistrationOrigin"/> y no un origen propio porque esta membresía
    /// **es** la del owner del tenant, el mismo caso que <c>TenantRegistrationService</c>: así
    /// hereda la protección del agregado, que impide suspenderla, quitarla o dejarla sin el rol
    /// admin. La contrapartida es que tampoco se puede quitar por la API — correcto para un
    /// tenant cuya única salida es borrar la base y volver a sembrarlo.
    /// </summary>
    public static async Task SeedOwnerMembershipAsync(
        this IServiceProvider services,
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        var tenantId = new TenantId(SeedTenantId);
        if (await dbContext.Memberships.AnyAsync(
            membership => membership.TenantId == tenantId && membership.UserId == ownerUserId,
            cancellationToken))
        {
            return;
        }

        dbContext.Memberships.Add(Membership.CreateActive(
            MembershipId.New(),
            ownerUserId,
            tenantId,
            ["admin"],
            Membership.RegistrationOrigin,
            DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync(cancellationToken);
    }
```

- [ ] **Step 4: Llamarlo desde el orquestador**

En `RunQepSeedAsync`, después de obtener `ownerUserId`:

```csharp
        await services.SeedOwnerMembershipAsync(ownerUserId, cancellationToken);
```

- [ ] **Step 5: Correr y verificar que pasa**

```powershell
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~SeedStartupTests"
```

Esperado: **PASS**, 5 de 5.

- [ ] **Step 6: Commit**

```bash
git add src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Seed/TenancySeeder.cs src/Bootstrapper/Seeding/QepSeedRunner.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/SeedStartupTests.cs
git commit -m "feat(tenancy): sembrar la membresia admin del owner"
```

---

### Task 5: Seeder del catálogo

**Files:**
- Create: `src/Modules/Catalog/Modules.Catalog.Infrastructure/Seed/Data/catalog-products.json` (mover con `git mv` desde `ops/seed/`)
- Create: `src/Modules/Catalog/Modules.Catalog.Infrastructure/Seed/CatalogSeedFile.cs`
- Create: `src/Modules/Catalog/Modules.Catalog.Infrastructure/Seed/CatalogSeeder.cs`
- Modify: `src/Modules/Catalog/Modules.Catalog.Infrastructure/Modules.Catalog.Infrastructure.csproj`
- Modify: `src/Bootstrapper/Seeding/QepSeedRunner.cs`
- Test: `tests/Modules/Catalog/Modules.Catalog.IntegrationTests/CatalogSeedTests.cs`

**Interfaces:**
- Consumes: `TenancySeeder.SeedTenantId`.
- Produces: `IServiceProvider.SeedCatalogAsync(Guid tenantId, CancellationToken)`.

- [ ] **Step 1: Mover el JSON y declararlo como recurso embebido**

```bash
mkdir -p src/Modules/Catalog/Modules.Catalog.Infrastructure/Seed/Data
git mv ops/seed/catalog-products.json src/Modules/Catalog/Modules.Catalog.Infrastructure/Seed/Data/catalog-products.json
```

En `Modules.Catalog.Infrastructure.csproj`, agregar el mismo `ItemGroup` que ya tiene `Modules.Geography.Infrastructure`:

```xml
  <ItemGroup>
    <EmbeddedResource Include="Seed\Data\*.json" />
  </ItemGroup>
```

- [ ] **Step 2: Habilitar la prueba unitaria del parser**

Espeja exactamente lo que ya hace Geography con `DivipolaDataParserTests`. En
`Modules.Catalog.Infrastructure.csproj`:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="Modules.Catalog.UnitTests" />
  </ItemGroup>
```

Y en `tests/Modules/Catalog/Modules.Catalog.UnitTests/Modules.Catalog.UnitTests.csproj`, agregar
la referencia que hoy no tiene:

```xml
    <ProjectReference Include="..\..\..\..\src\Modules\Catalog\Modules.Catalog.Infrastructure\Modules.Catalog.Infrastructure.csproj" />
```

- [ ] **Step 3: Escribir la prueba unitaria del parser, que falla**

En `tests/Modules/Catalog/Modules.Catalog.UnitTests/CatalogSeedFileTests.cs`:

```csharp
using Modules.Catalog.Infrastructure.Seed;

namespace Modules.Catalog.UnitTests;

// Contra el recurso embebido real, no contra un JSON de prueba: lo que se verifica es que el
// archivo que se va a sembrar es el correcto y se lee bien. Corre en milisegundos, así que un
// JSON mal formado se detecta sin levantar PostgreSQL.
public sealed class CatalogSeedFileTests
{
    [Fact]
    public void ReadsEveryProductFromTheEmbeddedResource()
    {
        var seed = CatalogSeeder.ReadSeedFile();

        Assert.Equal("IVA 19%", seed.TaxRate.Name);
        Assert.Equal(19, seed.TaxRate.Percentage);
        Assert.Equal(19, seed.Products.Count);
        Assert.Equal(19, seed.Products.Select(product => product.Sku).Distinct().Count());
        Assert.All(seed.Products, product => Assert.False(string.IsNullOrWhiteSpace(product.Name)));
        // Todo producto necesita precio en al menos una moneda: Product.ApplyPricing lo exige
        // incondicionalmente, así que un archivo sin precio revienta recién al sembrar.
        Assert.All(
            seed.Products,
            product => Assert.True(product.PriceCop is not null || product.PriceUsd is not null));

        var bronceador = seed.Products.Single(product => product.Sku == "7416");
        Assert.Equal(35900m, bronceador.PriceCop);
        Assert.Equal(9.97m, bronceador.PriceUsd);
    }
}
```

Esto obliga a que `ReadSeedFile` sea `internal static` (no `private`) en `CatalogSeeder`.

- [ ] **Step 4: Correr y verificar que falla**

```powershell
dotnet test tests/Modules/Catalog/Modules.Catalog.UnitTests --filter "FullyQualifiedName~CatalogSeedFileTests"
```

Esperado: **FAIL** de compilación — `CatalogSeeder` no existe todavía. Copiar la salida.

- [ ] **Step 5: Escribir la prueba de integración, que falla**

En `tests/Modules/Catalog/Modules.Catalog.IntegrationTests/CatalogSeedTests.cs`, con el mismo `StartDatabaseAsync` y `QepApiFactory` de `SeedStartupTests` (repetidos, porque cada archivo de pruebas de este repositorio tiene su propia factory privada):

```csharp
    [Fact]
    public async Task SeedCreatesTheTaxRateAndEveryProduct()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), seedEnabled: true);
        using var client = factory.CreateClient();

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var taxRate = await dbContext.TaxRates.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("IVA 19%", taxRate.Name);
        Assert.Equal(19, taxRate.Percentage);

        var products = await dbContext.Products.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(19, products.Count);
        // `.Value` explícito: Product.TaxRateId es TaxRateId? y taxRate.Id es TaxRateId, así que
        // sin esto la sobrecarga que elige el compilador compara por object y la aserción miente.
        Assert.All(products, product => Assert.Equal(taxRate.Id, product.TaxRateId!.Value));
        Assert.All(products, product => Assert.True(product.IsActive));

        var bronceador = products.Single(product => product.Code == "7416");
        Assert.Equal(35900m, bronceador.PriceBaseCop);
        Assert.Equal(9.97m, bronceador.PriceBaseUsd);
        // Ocho de los diecinueve nombres llevan tilde; si el recurso embebido se lee con la
        // codificación equivocada, esta es la aserción que lo detecta.
        Assert.Equal(
            "COMBO ROSADO RITUAL DE SEDUCCIÓN",
            products.Single(product => product.Code == "3001").Name);
    }
```

- [ ] **Step 6: Correr y verificar que falla**

```powershell
dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests --filter "FullyQualifiedName~CatalogSeedTests"
```

Esperado: **FAIL** — `CatalogDbContext.TaxRates` está vacío.

- [ ] **Step 7: Crear el modelo del JSON**

```csharp
namespace Modules.Catalog.Infrastructure.Seed;

// Sólo los campos que se siembran. `_note` y `notSeeded` del archivo se ignoran solos: el
// deserializador descarta lo que no mapea, y esos existen como referencia para quien lea el
// JSON, no como datos.
internal sealed record CatalogSeedFile(
    CatalogSeedTaxRate TaxRate,
    IReadOnlyList<CatalogSeedProduct> Products);

internal sealed record CatalogSeedTaxRate(string Name, int Percentage);

internal sealed record CatalogSeedProduct(
    string Sku,
    string Name,
    decimal? PriceCop,
    decimal? PriceUsd);
```

- [ ] **Step 8: Crear `CatalogSeeder`**

```csharp
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Domain;
using Modules.Catalog.Infrastructure.Persistence;

namespace Modules.Catalog.Infrastructure.Seed;

/// <summary>
/// La mitad de Catalog de la semilla de arranque. Construye los agregados con
/// <c>TaxRate.Create</c> y <c>Product.Create</c>, así que todos los invariantes del dominio
/// siguen valiendo — lo único que se saltea respecto de un POST es la capa HTTP.
///
/// Idempotente por código de producto y por nombre de tasa, mismo criterio que
/// <c>GeographySeeder</c> con <c>DivipolaCode</c>.
/// </summary>
public static class CatalogSeeder
{
    private const string ResourceSuffix = "Seed.Data.catalog-products.json";

    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    public static async Task SeedCatalogAsync(
        this IServiceProvider services,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var seed = ReadSeedFile();
        var now = DateTimeOffset.UtcNow;

        var taxRate = await dbContext.TaxRates.SingleOrDefaultAsync(
            candidate => candidate.TenantId == tenantId && candidate.Name == seed.TaxRate.Name,
            cancellationToken);
        if (taxRate is null)
        {
            taxRate = TaxRate.Create(
                TaxRateId.New(), tenantId, seed.TaxRate.Name, seed.TaxRate.Percentage, now);
            dbContext.TaxRates.Add(taxRate);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var existingCodes = await dbContext.Products
            .Where(product => product.TenantId == tenantId)
            .Select(product => product.Code)
            .ToListAsync(cancellationToken);
        var existing = new HashSet<string>(existingCodes, StringComparer.Ordinal);

        var added = false;
        foreach (var product in seed.Products)
        {
            if (!existing.Add(product.Sku))
            {
                continue;
            }

            dbContext.Products.Add(Product.Create(
                ProductId.New(),
                tenantId,
                product.Name,
                product.Sku,
                new ProductDetails { TaxRateId = taxRate.Id },
                new ProductPricing { BaseUsd = product.PriceUsd, BaseCop = product.PriceCop },
                now));
            added = true;
        }

        if (added)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    // internal y no private: CatalogSeedFileTests lo llama directo, via InternalsVisibleTo.
    internal static CatalogSeedFile ReadSeedFile()
    {
        var assembly = typeof(CatalogSeeder).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(ResourceSuffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Embedded catalog seed resource ending with '{ResourceSuffix}' was not found "
                + $"in assembly '{assembly.FullName}'.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded catalog seed resource '{resourceName}' could not be opened.");

        return JsonSerializer.Deserialize<CatalogSeedFile>(stream, SerializerOptions)
            ?? throw new InvalidOperationException(
                $"Embedded catalog seed resource '{resourceName}' deserialized to null.");
    }
}
```

- [ ] **Step 9: Llamarlo desde el orquestador**

En `RunQepSeedAsync`, después de `SeedOwnerMembershipAsync`:

```csharp
        await services.SeedCatalogAsync(TenancySeeder.SeedTenantId, cancellationToken);
```

y agregar `using Modules.Catalog.Infrastructure.Seed;`.

- [ ] **Step 10: Correr y verificar que pasa**

```powershell
dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests --filter "FullyQualifiedName~CatalogSeedTests"
```

Esperado: **PASS**. Si falla por tildes, revisar que el JSON esté en UTF-8 y que el `EmbeddedResource` se haya declarado.

- [ ] **Step 11: Commit**

```bash
git add src/Modules/Catalog tests/Modules/Catalog ops/seed
git commit -m "feat(catalog): sembrar la tasa y los productos desde el recurso embebido"
```

---

### Task 6: Idempotencia de punta a punta

La que más importa: el ambiente se borra y se vuelve a desplegar, y la aplicación reinicia sola en k8s. Sembrar dos veces tiene que dejar exactamente el mismo estado.

**Files:**
- Test: `tests/Modules/Catalog/Modules.Catalog.IntegrationTests/CatalogSeedTests.cs`

- [ ] **Step 1: Escribir la prueba que falla**

```csharp
    // La app reinicia sola en k8s, así que la semilla corre muchas veces sobre la misma base.
    // Se llama al orquestador de nuevo sobre la misma base en vez de levantar una segunda
    // factory: eso es exactamente lo que hace un reinicio de pod.
    [Fact]
    public async Task SeedingTwiceLeavesTheSameState()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), seedEnabled: true);
        using var client = factory.CreateClient();

        await factory.Services.RunQepSeedAsync(TestContext.Current.CancellationToken);

        await using var scope = factory.Services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var identity = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        Assert.Equal(19, await catalog.Products.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await catalog.TaxRates.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            1,
            await tenancy.Memberships.CountAsync(
                membership => membership.TenantId == new TenantId(TenancySeeder.SeedTenantId),
                TestContext.Current.CancellationToken));
        Assert.Equal(
            1,
            await identity.Users.CountAsync(
                user => user.Email == "semilla@qcode.co", TestContext.Current.CancellationToken));
    }
```

- [ ] **Step 2: Correr**

```powershell
dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests --filter "FullyQualifiedName~SeedingTwiceLeavesTheSameState"
```

Si **PASA de entrada**, los cuatro seeders ya eran idempotentes: dejarlo asentado en el commit, porque es la prueba que impide que alguien rompa esa propiedad después. Si **FALLA**, arreglar el seeder que duplicó y volver a correr.

- [ ] **Step 3: Correr la suite completa**

```powershell
dotnet build
dotnet test
```

Esperado: todo verde, incluidos `ArchitectureTests` — ningún seeder agregó una referencia entre módulos.

- [ ] **Step 4: Commit**

```bash
git add tests/Modules/Catalog/Modules.Catalog.IntegrationTests/CatalogSeedTests.cs
git commit -m "test(seed): verificar que sembrar dos veces deja el mismo estado"
```

---

### Task 7: Borrar el script y actualizar la documentación

El borrado va acá y no antes: hasta la Task 6, `ops/seed/` era el único mecanismo que existía.

**Files:**
- Delete: `ops/seed/Seed-CatalogProducts.ps1` (y `ops/seed/`, que queda vacío)
- Modify: `README.md` (reescribir la sección "Semilla de catálogo")
- Modify: `k8s/prod-configMap.yaml` (documentar las dos claves)

- [ ] **Step 1: Borrar el script**

```bash
git rm ops/seed/Seed-CatalogProducts.ps1
```

- [ ] **Step 2: Reescribir la sección del README**

Reemplazar el cuerpo entero de "## Semilla de catálogo" (todo lo que hay entre ese encabezado y
"## API implementada") por:

```markdown
## Semilla de arranque

Con `Seed:Enabled` en `true`, la aplicación deja el ambiente utilizable al arrancar:
crea el tenant **Origen botánico**, el usuario que lo administra, su membresía y el
catálogo de diecinueve productos con la tasa `IVA 19%`. Pensada para el ambiente
desplegado durante el desarrollo, donde la base se borra y se vuelve a crear: después
de un borrado no hay ningún paso manual, alcanza con que la aplicación reinicie.

| Clave              | Por defecto | Qué hace                                              |
| ------------------ | ----------- | ----------------------------------------------------- |
| `Seed__Enabled`    | `false`     | Interruptor único. Apagado, no se siembra nada        |
| `Seed__OwnerEmail` | sin valor   | Email que recibe la membresía con rol `admin`         |

El usuario se siembra **sólo con su email**, sin proveedor vinculado: el primer login
con Google lo vincula solo, porque `ProviderLinkingService` busca por email verificado.
No hace falta invitación ni registrar un tenant.

> [!WARNING]
> La semilla **crea un tenant y otorga el rol `admin`**. Es un mecanismo que concede
> privilegios, y a diferencia del stub de autenticación no puede negarse a arrancar
> fuera de `Development`, porque el ambiente desplegado corre como `Production`. Su
> única defensa es que nace apagada. **Al entregar el ambiente al cliente hay que
> borrar las dos claves del ConfigMap.**

Es idempotente: el tenant por id, el usuario por email, la membresía por el par
usuario-tenant y los productos por código. Correrla muchas veces —cada reinicio de pod
lo hace— no duplica nada. Tampoco actualiza: cambiar un precio ya sembrado es un `PUT`,
no una segunda corrida.

Tres campos del archivo de origen no se cargan porque el dominio no los tiene: peso
neto, peso bruto y unidad de empaque. La imagen tampoco: en el origen es una ruta, y
`Product.ImageFileId` es un archivo de la [biblioteca](#biblioteca-de-archivos-cloudflare-r2),
que se sube aparte. Los cuatro quedan en
[`catalog-products.json`](src/Modules/Catalog/Modules.Catalog.Infrastructure/Seed/Data/catalog-products.json)
bajo `notSeeded`, como referencia.

Para levantarla en local:

```powershell
$env:Seed__Enabled = "true"
$env:Seed__OwnerEmail = "<tu-email>"
dotnet run --project src/Api --launch-profile http
```
```

Y actualizar el enlace de la tabla de contenidos o de cualquier otra sección que apunte a
"Semilla de catálogo", si existe.

- [ ] **Step 3: Agregar las claves al ConfigMap**

En `k8s/prod-configMap.yaml`, con un comentario que diga que se borran al entregar el ambiente:

```yaml
  # Semilla del ambiente de desarrollo: crea el tenant "Origen botánico", su usuario owner y el
  # catálogo. Concede el rol admin, así que ESTAS DOS CLAVES SE BORRAN al entregar el ambiente
  # al cliente. Sin ellas la semilla no corre.
  Seed__Enabled: "true"
  Seed__OwnerEmail: "#{SEED_OWNER_EMAIL}#"
```

- [ ] **Step 4: Verificar que no quedaron referencias colgadas**

```bash
grep -rn "ops/seed\|Seed-CatalogProducts" --exclude-dir=.git .
```

Esperado: sin resultados fuera de `docs/superpowers/`.

- [ ] **Step 5: Compilar y correr todo**

```powershell
dotnet build
dotnet test
```

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor(seed): reemplazar el script de PowerShell por la semilla de arranque"
```

---

## Verificación final

Con la implementación completa, probar el camino real en local:

```powershell
docker compose up -d
$env:Seed__Enabled = "true"
$env:Seed__OwnerEmail = "andres.jaramillo@qcode.co"
$env:Authentication__UseDevelopmentStub = "true"
dotnet run --project src/Api --launch-profile http
```

Y confirmar contra la base:

```powershell
docker exec qep-local-postgres-1 psql -U qep -d qep -c "select count(*) from catalog.products;"
docker exec qep-local-postgres-1 psql -U qep -d qep -c "select slug, display_name from tenancy.tenants;"
```

Esperado: 19 productos y el tenant `origen-botanico`. En los logs del arranque tiene que verse la advertencia con el slug y el email.
