using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary><c>{nombre}</c> pasa a <c>{nombre_contacto}</c> en los cuerpos guardados. Las pruebas siembran en la migración anterior, que es la
/// forma que tiene producción cuando ésta corre. La columna es <c>varchar(4096)</c> y el reemplazo agrega 9 caracteres: una fila que no quepa
/// no puede tumbar el despliegue entero.</summary>
public sealed class QuickReplyVariableMigrationTests
{
    private const string MigrationSuffix = "_RenameQuickReplyNombreVariable";

    private const long SeededVersion = 3;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task SeedAsync(string connectionString, string shortcut, string body) =>
        await ExecuteAsync(connectionString,
            """
            INSERT INTO messaging.quick_replies (id, tenant_id, shortcut, body, created_at, updated_at, version)
            VALUES (@id, @tenant, @shortcut, @body, now(), now(), @version)
            """,
            ("id", Guid.CreateVersion7()), ("tenant", Guid.CreateVersion7()), ("shortcut", shortcut), ("body", body), ("version", SeededVersion));

    private static Task<string> BodyAsync(string connectionString, string shortcut) =>
        ScalarAsync<string>(connectionString, "SELECT body FROM messaging.quick_replies WHERE shortcut = @shortcut", ("shortcut", shortcut));

    private static Task<long> VersionAsync(string connectionString, string shortcut) =>
        ScalarAsync<long>(connectionString, "SELECT version FROM messaging.quick_replies WHERE shortcut = @shortcut", ("shortcut", shortcut));

    private static async Task<(IMigrator Migrator, string Previous)> RollBackToBeforeTheRenameAsync(IServiceScope scope)
    {
        var context = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var migrations = context.Database.GetMigrations().ToList();
        var rename = migrations.Single(id => id.EndsWith(MigrationSuffix, StringComparison.Ordinal));
        var previous = migrations[migrations.IndexOf(rename) - 1];
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(previous, Ct);
        return (migrator, previous);
    }

    [Fact]
    public async Task UpRenamesTheVariableAndTouchesNothingElse()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        using var scope = factory.Services.CreateScope();
        var (migrator, _) = await RollBackToBeforeTheRenameAsync(scope);
        await SeedAsync(connectionString, "una", "Hola {nombre}, soy {asesor} de {empresa}.");
        await SeedAsync(connectionString, "dos", "{nombre}{nombre}");
        await SeedAsync(connectionString, "dobles", "{{nombre}}");
        await SeedAsync(connectionString, "parecidos", "Nombre: {nombres} {Nombre} { nombre } nombre {nombre_x}");
        await SeedAsync(connectionString, "nueva", "{saludo} {nombre_contacto}");

        await migrator.MigrateAsync(null, Ct);

        Assert.Equal("Hola {nombre_contacto}, soy {asesor} de {empresa}.", await BodyAsync(connectionString, "una"));
        Assert.Equal("{nombre_contacto}{nombre_contacto}", await BodyAsync(connectionString, "dos"));
        Assert.Equal("{{nombre_contacto}}", await BodyAsync(connectionString, "dobles"));
        Assert.Equal("Nombre: {nombres} {Nombre} { nombre } nombre {nombre_x}", await BodyAsync(connectionString, "parecidos"));
        Assert.Equal("{saludo} {nombre_contacto}", await BodyAsync(connectionString, "nueva"));

        // La versión sube sólo donde el cuerpo cambió: un formulario abierto con el texto viejo recibe 412 en vez de pisarlo.
        Assert.Equal(SeededVersion + 1, await VersionAsync(connectionString, "una"));
        Assert.Equal(SeededVersion, await VersionAsync(connectionString, "parecidos"));
        Assert.Equal(SeededVersion, await VersionAsync(connectionString, "nueva"));
    }

    [Fact]
    public async Task ABodyThatWouldNotFitKeepsTheOldVariableInsteadOfAbortingTheMigration()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        using var scope = factory.Services.CreateScope();
        var (migrator, _) = await RollBackToBeforeTheRenameAsync(scope);
        const string Variable = "{nombre}";
        const int Growth = 9;
        var justFits = new string('a', QuickReply.BodyMaxLength - Growth - Variable.Length) + Variable;
        var oneTooMany = new string('a', QuickReply.BodyMaxLength - Growth - Variable.Length + 1) + Variable;
        await SeedAsync(connectionString, "cabe", justFits);
        await SeedAsync(connectionString, "nocabe", oneTooMany);

        await migrator.MigrateAsync(null, Ct);

        Assert.Equal(QuickReply.BodyMaxLength, (await BodyAsync(connectionString, "cabe")).Length);
        Assert.EndsWith("{nombre_contacto}", await BodyAsync(connectionString, "cabe"), StringComparison.Ordinal);
        Assert.Equal(oneTooMany, await BodyAsync(connectionString, "nocabe"));
        Assert.Equal(SeededVersion, await VersionAsync(connectionString, "nocabe"));
    }

    // QuickReply valida string.Length (unidades UTF-16): un emoji son dos unidades y un solo code point. Contar code points dejaría pasar un
    // cuerpo que el agregado rechaza después con 422 invalid_body en el siguiente PUT.
    [Fact]
    public async Task ABodyWithEmojisIsMeasuredInTheUnitsTheAggregateValidates()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        using var scope = factory.Services.CreateScope();
        var (migrator, _) = await RollBackToBeforeTheRenameAsync(scope);
        const string Variable = "{nombre}";
        const string Emoji = "\U0001F600";
        const int Growth = 9;
        var emojis = string.Concat(Enumerable.Repeat(Emoji, (QuickReply.BodyMaxLength - Growth - Variable.Length - 1) / Emoji.Length));
        var justFits = "a" + emojis + Variable;
        var oneTooMany = "aa" + emojis + Variable;
        Assert.Equal((QuickReply.BodyMaxLength - Growth, QuickReply.BodyMaxLength - Growth + 1), (justFits.Length, oneTooMany.Length));
        await SeedAsync(connectionString, "cabe", justFits);
        await SeedAsync(connectionString, "nocabe", oneTooMany);

        await migrator.MigrateAsync(null, Ct);

        var fitted = await BodyAsync(connectionString, "cabe");
        Assert.Equal(QuickReply.BodyMaxLength, fitted.Length);
        Assert.EndsWith("{nombre_contacto}", fitted, StringComparison.Ordinal);
        Assert.Equal(oneTooMany, await BodyAsync(connectionString, "nocabe"));
        Assert.Equal(SeededVersion, await VersionAsync(connectionString, "nocabe"));
    }

    // El Down deja los cuerpos que un SPA viejo sabe resolver, incluidos los que nacieron con {nombre_contacto} después del Up.
    [Fact]
    public async Task DownPutsTheOldVariableBack()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        using var scope = factory.Services.CreateScope();
        var (migrator, previous) = await RollBackToBeforeTheRenameAsync(scope);
        await SeedAsync(connectionString, "una", "Hola {nombre}, soy {asesor}.");
        await SeedAsync(connectionString, "parecidos", "{nombre_x} {Nombre_Contacto}");
        await migrator.MigrateAsync(null, Ct);
        await SeedAsync(connectionString, "nueva", "{saludo} {nombre_contacto}");

        await migrator.MigrateAsync(previous, Ct);

        Assert.Equal("Hola {nombre}, soy {asesor}.", await BodyAsync(connectionString, "una"));
        Assert.Equal("{nombre_x} {Nombre_Contacto}", await BodyAsync(connectionString, "parecidos"));
        Assert.Equal("{saludo} {nombre}", await BodyAsync(connectionString, "nueva"));
        Assert.Equal(SeededVersion, await VersionAsync(connectionString, "parecidos"));
    }
}
