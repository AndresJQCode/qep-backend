using Microsoft.Extensions.Configuration;
using Modules.Messaging.Infrastructure.Realtime;

namespace Modules.Messaging.UnitTests;

/// <summary>El listener de LISTEN usa <c>QepDatabaseListener</c> (directo a Postgres) y cae a <c>QepDatabase</c> si no hay una válida.</summary>
public sealed class ListenerConnectionStringTests
{
    private const string Shared = "Host=pooler;Database=qep";
    private const string Direct = "Host=direct;Database=qep";

    private static IConfiguration Config(string? listener, string? shared = Shared)
    {
        var values = new Dictionary<string, string?>();
        if (shared is not null)
        {
            values["ConnectionStrings:QepDatabase"] = shared;
        }

        if (listener is not null)
        {
            values["ConnectionStrings:QepDatabaseListener"] = listener;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void TheListenerKeyWinsWhenItHasAValue()
    {
        var (connectionString, keyName) = ListenerConnectionString.Resolve(Config(Direct));

        Assert.Equal(Direct, connectionString);
        Assert.Equal("QepDatabaseListener", keyName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#{DATABASE_LISTENER_CONNECTION_STRING}#")]
    [InlineData("  #{DATABASE_LISTENER_CONNECTION_STRING}#  ")]
    public void ItFallsBackToTheSharedKeyWhenTheListenerKeyIsAbsentBlankOrAnUnresolvedToken(string? listener)
    {
        var (connectionString, keyName) = ListenerConnectionString.Resolve(Config(listener));

        Assert.Equal(Shared, connectionString);
        Assert.Equal("QepDatabase", keyName);
    }

    [Fact]
    public void WithoutAnyKeyItKeepsTheExistingError()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => ListenerConnectionString.Resolve(Config(null, null)));

        Assert.Equal("Connection string 'QepDatabase' is required.", exception.Message);
    }
}
