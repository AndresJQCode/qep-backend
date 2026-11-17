using Modules.Support.Application;

namespace Modules.Support.UnitTests;

/// <summary>Fase 4b, Tarea 13 (H19, H20): la calificación es el nombre exacto de una de las tres (<c>Good</c>, <c>Regular</c>,
/// <c>Bad</c>) y el comentario, opcional, tiene hasta 1000 caracteres recortado y sin caracteres de control salvo el salto de línea. Igual
/// por el enlace del correo y por la página de consulta.</summary>
public sealed class PublicSurveyValidatorTests
{
    private static IEnumerable<string> LinkErrors(string? rating, string? comment) =>
        new AnswerPublicSurveyValidator()
            .Validate(new AnswerPublicSurveyCommand("mi-empresa", "token", rating, comment))
            .Errors.Select(error => error.PropertyName).Distinct();

    private static IEnumerable<string> ConsultErrors(string? rating, string? comment) =>
        new RatePublicTicketValidator()
            .Validate(new RatePublicTicketCommand("mi-empresa", "127.0.0.1", "SOP-000001", "123456", "ok", rating, comment))
            .Errors.Select(error => error.PropertyName).Distinct();

    [Fact]
    public void TheRatingIsOneOfThreeAndTheCommentIsAtMost1000()
    {
        foreach (var errors in new Func<string?, string?, IEnumerable<string>>[] { LinkErrors, ConsultErrors })
        {
            Assert.Empty(errors("Good", null));
            Assert.Empty(errors("Regular", ""));
            Assert.Empty(errors("Bad", $"  {new string('a', 1000)}  "));
            Assert.Empty(errors("Good", "Primera línea\r\nSegunda"));

            Assert.Equal(["Rating"], errors(null, null));
            Assert.Equal(["Rating"], errors("good", null));
            Assert.Equal(["Rating"], errors("Buena", null));
            Assert.Equal(["Rating"], errors("0", null));
            Assert.Equal(["Comment"], errors("Good", new string('a', 1001)));
            Assert.Equal(["Comment"], errors("Good", "Con un \t tabulador"));
            Assert.Equal(["Comment"], errors("Good", "Con un \0 nulo"));
        }
    }
}
