using FluentValidation.TestHelper;
using Modules.Catalog.Application;

namespace Modules.Catalog.UnitTests;

/// <summary>
/// Tarea 7: el cuerpo de la copia de escalas (<c>sourceVariantId</c> + <c>targetVariantIds</c>). El
/// 422 lleva el mapa <c>errors</c> por campo; el tope de 200 se cuenta en variantes.
/// </summary>
public sealed class CopyPriceScalesValidatorTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid Source = Guid.CreateVersion7();

    private static CopyPriceScalesCommand CommandWith(int targets) =>
        new(TenantId, Source, Enumerable.Range(0, targets).Select(_ => Guid.CreateVersion7()).ToArray());

    [Fact]
    public void AcceptsTwoHundredTargets()
    {
        new CopyPriceScalesValidator().TestValidate(CommandWith(PriceScaleCopyLimits.MaxTargets))
            .ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void RejectsTwoHundredAndOneTargets()
    {
        var result = new CopyPriceScalesValidator().TestValidate(CommandWith(PriceScaleCopyLimits.MaxTargets + 1));

        result.ShouldHaveValidationErrorFor(command => command.TargetVariantIds);
    }

    [Fact]
    public void RejectsAnEmptySource()
    {
        var result = new CopyPriceScalesValidator().TestValidate(
            new CopyPriceScalesCommand(TenantId, Guid.Empty, [Guid.CreateVersion7()]));

        result.ShouldHaveValidationErrorFor(command => command.SourceVariantId);
    }

    [Fact]
    public void RejectsAnEmptyTargetList()
    {
        var result = new CopyPriceScalesValidator().TestValidate(new CopyPriceScalesCommand(TenantId, Source, []));

        result.ShouldHaveValidationErrorFor(command => command.TargetVariantIds);
    }

    [Fact]
    public void RejectsAnEmptyTargetId()
    {
        var result = new CopyPriceScalesValidator().TestValidate(
            new CopyPriceScalesCommand(TenantId, Source, [Guid.CreateVersion7(), Guid.Empty]));

        result.ShouldHaveValidationErrorFor(command => command.TargetVariantIds);
    }

    [Fact]
    public void RejectsARepeatedTarget()
    {
        var repeated = Guid.CreateVersion7();

        var result = new CopyPriceScalesValidator().TestValidate(
            new CopyPriceScalesCommand(TenantId, Source, [repeated, repeated]));

        result.ShouldHaveValidationErrorFor(command => command.TargetVariantIds);
    }

    [Fact]
    public void RejectsTheSourceInsideTheTargets()
    {
        var result = new CopyPriceScalesValidator().TestValidate(
            new CopyPriceScalesCommand(TenantId, Source, [Guid.CreateVersion7(), Source]));

        result.ShouldHaveValidationErrorFor(command => command.TargetVariantIds);
    }
}
