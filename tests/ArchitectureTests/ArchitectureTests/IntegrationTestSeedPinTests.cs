using System.Text.RegularExpressions;

namespace ArchitectureTests;

/// <summary>
/// Every integration test host pins <c>Seed:Enabled</c>. The hosts run in <c>Development</c>, which loads the user-secrets of
/// <c>src/Api/Api.csproj</c>, and a developer with <c>Seed:Enabled=true</c> there seeds the development and operator tenants into every
/// test database: counts and <c>Single()</c> that were right on a clean machine break on that one (2026-11-16, Catalog, Quotations,
/// Tenancy). The seed tests still turn it on through <c>WithWebHostBuilder</c>, which is applied later and wins.
///
/// A source scan, not reflection: the factories are private nested classes of each test project. The check is per file, so a
/// file with two hosts needs two pins.
/// </summary>
public sealed class IntegrationTestSeedPinTests
{
    private static readonly Regex HostOverride = new(@"override\s+void\s+ConfigureWebHost\s*\(", RegexOptions.Compiled);

    private static readonly Regex SeedPin = new(@"UseSetting\(\s*""Seed:Enabled""", RegexOptions.Compiled);

    [Fact]
    public void EveryIntegrationTestHostPinsTheSeed()
    {
        var files = IntegrationTestSources().ToArray();

        var unpinned = files
            .Where(file => File.ReadAllText(file) is var source && HostOverride.Count(source) > SeedPin.Count(source))
            .Select(file => Path.GetRelativePath(RepositoryRoot(), file))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(files);
        Assert.True(
            unpinned.Length == 0,
            "These test hosts do not pin Seed:Enabled, so the developer's user-secrets can seed their database: "
                + string.Join(", ", unpinned));
    }

    private static IEnumerable<string> IntegrationTestSources() =>
        Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot(), "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Backend.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"Backend.slnx was not found walking up from {AppContext.BaseDirectory}.");
    }
}
