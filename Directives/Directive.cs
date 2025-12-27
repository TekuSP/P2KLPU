/// <summary>
/// Parsed in-file directive from a <c>;P2KLPU ...</c> comment.
/// </summary>
/// <remarks>
/// Directives are intentionally kept as raw key/value strings and interpreted later by
/// <see cref="DirectiveParseResult.ApplyTo"/>.
/// </remarks>
/// <seealso cref="P2klpuDirectiveScanner"/>
/// <seealso cref="DirectiveParseResult"/>
sealed record Directive(string Raw, string Key, string Value);
