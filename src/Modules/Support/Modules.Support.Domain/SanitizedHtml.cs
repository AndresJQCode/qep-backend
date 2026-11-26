namespace Modules.Support.Domain;

/// <summary>
/// The output of <c>ISupportHtmlSanitizer</c> (spec 2026-11-15 §6.2). It lives in Domain because
/// <see cref="SupportLegalDocumentVersion.Create"/> takes it: a version can only be built from HTML that already went through the
/// sanitizer.
/// </summary>
/// <param name="Html">The canonical, sanitized HTML: sanitizing it again gives the same bytes.</param>
/// <param name="HasVisibleText">Whether the sanitized DOM has any text left once spaces and <c>&amp;nbsp;</c> are trimmed.</param>
public sealed record SanitizedHtml(string Html, bool HasVisibleText);
