using Modules.Hr.Application;

namespace Modules.Hr.UnitTests;

public sealed class HrTextTests
{
    [Fact]
    public void FoldRemovesAccentsCaseAndExtraSpaces()
    {
        Assert.Equal("termino fijo", HrText.Fold("  Término   FIJO "));
        Assert.Null(HrText.Fold("   "));
        Assert.Null(HrText.Fold(null));
    }
}
