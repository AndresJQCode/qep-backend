using Modules.Reporting.Application;
using Modules.Reporting.Domain;

namespace Modules.Reporting.UnitTests;

/// <summary>
/// Los dos limites de la exportacion, con sus codigos de dominio exactos: el frontend los
/// distingue por <c>code</c>, no por el texto.
/// </summary>
public sealed class ReportExportRulesTests
{
    [Fact]
    public void AnEmptyExportFails()
    {
        var error = Assert.Throws<ReportingDomainException>(
            () => ReportExportRules.EnsureExportable(0));

        Assert.Equal("reporting.export.empty", error.Code);
    }

    [Fact]
    public void AnExportOverTheCapFails()
    {
        var error = Assert.Throws<ReportingDomainException>(
            () => ReportExportRules.EnsureExportable(ReportExportRules.MaxExportRows + 1));

        Assert.Equal("reporting.export.too_many_rows", error.Code);
    }

    /// <summary>Exactamente el tope entra. Es el borde que el "uno de mas" de
    /// <see cref="ReportExportRules.ExportProbeLimit"/> existe para distinguir.</summary>
    [Fact]
    public void ExactlyTheCapIsAllowed()
    {
        ReportExportRules.EnsureExportable(ReportExportRules.MaxExportRows);
        ReportExportRules.EnsureExportable(1);
    }

    [Fact]
    public void TheProbeLimitAsksForOneRowMoreThanTheCap()
    {
        Assert.Equal(50_000, ReportExportRules.MaxExportRows);
        Assert.Equal(ReportExportRules.MaxExportRows + 1, ReportExportRules.ExportProbeLimit);
    }
}
