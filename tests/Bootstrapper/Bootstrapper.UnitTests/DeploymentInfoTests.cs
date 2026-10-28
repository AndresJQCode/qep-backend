using Bootstrapper.Deployment;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Bootstrapper.UnitTests;

/// <summary>
/// La identidad del build llega al pod por el ConfigMap con tokens que el pipeline reemplaza
/// (<c>#{Build.BuildId}#</c> y compañía, spec 2026-10-14). Si la plantilla no reemplaza uno, el
/// literal llega igual y el endpoint tiene que decir "no sé" (<c>null</c>) en vez de publicarlo:
/// un token a medio resolver que parece un dato es peor que un campo vacío.
/// </summary>
public sealed class DeploymentInfoTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#{Build.BuildId}#")]
    [InlineData("  #{Build.SourceVersion}#  ")]
    public void AnAbsentOrUnresolvedValueIsNull(string? raw)
    {
        Assert.Null(DeploymentInfo.Normalize(raw));
    }

    [Fact]
    public void AValueIsTrimmed()
    {
        Assert.Equal("4812", DeploymentInfo.Normalize(" 4812 "));
    }

    // Sin la sección —local, o un ConfigMap viejo— el pod arranca igual: la versión es
    // información, no una precondición. Lo que sí se sabe siempre es el ambiente y el arranque.
    [Fact]
    public void WithoutTheSectionTheBuildIsUnknownButTheRestIsNot()
    {
        var before = DateTimeOffset.UtcNow;

        var info = new DeploymentInfo(new DeploymentOptions(), new TestEnvironment("Development"));

        Assert.Null(info.BuildId);
        Assert.Null(info.BuildNumber);
        Assert.Null(info.Commit);
        Assert.Equal("Development", info.Environment);
        Assert.InRange(info.StartedAt, before, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void TheConfiguredBuildComesThroughNormalized()
    {
        var options = new DeploymentOptions
        {
            BuildId = "4812",
            BuildNumber = " 20261014.3 ",
            Commit = "#{Build.SourceVersion}#",
        };

        var info = new DeploymentInfo(options, new TestEnvironment("Production"));

        Assert.Equal("4812", info.BuildId);
        Assert.Equal("20261014.3", info.BuildNumber);
        Assert.Null(info.Commit);
        Assert.Equal("Production", info.Environment);
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
