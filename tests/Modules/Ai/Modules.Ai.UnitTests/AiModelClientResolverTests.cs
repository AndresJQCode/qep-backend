using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Modules.Ai.Application;
using Modules.Ai.Domain;
using Modules.Ai.Infrastructure;

namespace Modules.Ai.UnitTests;

/// <summary>Plan 2026-11-12, 5.2: cada proveedor de <see cref="AiProviders.All"/> tiene su adaptador, y uno sin adaptador frena el
/// arranque, no un trabajo a mitad de una conversación.</summary>
public sealed class AiModelClientResolverTests
{
    [Fact]
    public void EveryProviderResolvesItsAdapter()
    {
        using var provider = BuildServices().BuildServiceProvider();
        using var scope = provider.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IAiModelClientResolver>();

        foreach (var aiProvider in AiProviders.All)
        {
            Assert.NotNull(resolver.For(aiProvider));
        }

        Assert.IsType<Infrastructure.Model.Anthropic.ClaudeModelClient>(resolver.For(AiProviders.Anthropic));
        Assert.IsType<Infrastructure.Model.OpenAi.OpenAiModelClient>(resolver.For(AiProviders.OpenAi));
    }

    [Fact]
    public void AProviderWithoutAdapterFailsAtStartup()
    {
        var services = BuildServices();
        var openAi = services.Single(descriptor =>
            descriptor.ServiceType == typeof(IAiModelClient) && Equals(descriptor.ServiceKey, AiProviders.OpenAi.Value));
        services.Remove(openAi);
        using var provider = services.BuildServiceProvider();

        var failure = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains(AiProviders.OpenAi.Value, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheValidatorPassesWhenEveryProviderHasItsAdapter()
    {
        using var provider = BuildServices().BuildServiceProvider();

        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    private static ServiceCollection BuildServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:QepDatabase"] = "Host=localhost;Database=ai_unit_tests;Username=x;Password=x",
                ["Ai:DefaultProvider"] = "anthropic",
                ["Ai:Providers:anthropic:Models:0:Id"] = "claude-opus-5-5",
                ["Ai:Providers:anthropic:Models:0:Label"] = "Claude Opus 5.5",
                ["Ai:Providers:anthropic:Models:0:IsDefault"] = "true",
                ["Ai:Providers:anthropic:Models:0:Effort"] = "medium",
                ["Ai:Providers:openai:Models:0:Id"] = "gpt-6-luna",
                ["Ai:Providers:openai:Models:0:Label"] = "GPT-6 Luna",
                ["Ai:Providers:openai:Models:0:IsDefault"] = "true",
                ["Ai:Providers:openai:Models:0:Available"] = "false",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiInfrastructure(configuration);
        return services;
    }
}
