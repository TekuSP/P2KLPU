/// <summary>
/// Result of parsing CLI arguments.
/// </summary>
/// <remarks>
/// The CLI is intentionally minimal: most configuration comes from in-file directives.
/// </remarks>
/// <seealso cref="Cli"/>
/// <seealso cref="Options"/>
sealed record CliResult(bool Success, string? Error, Options Value);
