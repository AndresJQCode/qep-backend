using Bootstrapper.Startup;
using Microsoft.Extensions.Configuration;
using Modules.Messaging.Infrastructure.Realtime;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-10-29 (réplicas múltiples), D-2: el candado de arranque es un advisory lock de transacción y tiene que ir
/// directo a Postgres, igual que el <c>LISTEN</c> del listener de Messaging. <see cref="StartupLockConnectionString"/> es
/// una copia de <see cref="ListenerConnectionString"/> porque esa es <c>internal</c> de Messaging; esta prueba las amarra.
/// Si una cambia (un <c>Trim</c> o un <c>#{…}#</c> de menos) sin la otra, el candado podría pasar por PgBouncer sin que
/// nadie lo note.
/// </summary>
public sealed class StartupLockConnectionStringParityTests
{
    private const string Pooler = "Host=pooler;Database=qep";

    [Theory]
    [InlineData("Host=direct;Database=qep")]
    [InlineData("   Host=direct;Database=qep   ")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#{DATABASE_LISTENER_CONNECTION_STRING}#")]
    [InlineData(null)]
    public void TheLockConnectionDoesNotGoThroughThePooler(string? listener)
    {
        var values = new Dictionary<string, string?> { ["ConnectionStrings:QepDatabase"] = Pooler };
        if (listener is not null)
        {
            values["ConnectionStrings:QepDatabaseListener"] = listener;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        Assert.Equal(ListenerConnectionString.Resolve(configuration), StartupLockConnectionString.Resolve(configuration));
    }

    [Fact]
    public void BothRequireQepDatabase()
    {
        var configuration = new ConfigurationBuilder().Build();

        var listener = Assert.Throws<InvalidOperationException>(() => ListenerConnectionString.Resolve(configuration));
        var startup = Assert.Throws<InvalidOperationException>(() => StartupLockConnectionString.Resolve(configuration));

        Assert.Equal(listener.Message, startup.Message);
    }

    [Fact]
    public void BothUseTheSameKeyNames()
    {
        Assert.Equal(ListenerConnectionString.ListenerKey, StartupLockConnectionString.DirectKey);
        Assert.Equal(ListenerConnectionString.DefaultKey, StartupLockConnectionString.DefaultKey);
    }
}
