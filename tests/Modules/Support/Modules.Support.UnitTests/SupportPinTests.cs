using System.Text.RegularExpressions;
using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

// El PIN de consulta (plan fase 3, F7): seis dígitos aleatorios.
public sealed class SupportPinTests
{
    [Fact]
    public void ItIsSixDigits()
    {
        var pattern = new Regex(@"^\d{6}$", RegexOptions.None, TimeSpan.FromSeconds(1));

        for (var index = 0; index < 1000; index++)
        {
            Assert.Matches(pattern, SupportPin.Generate());
        }
    }
}
