using System;

/// <summary>
/// PrusaSlicer/Slic3r post-processing environment values.
/// </summary>
/// <remarks>
/// PrusaSlicer invokes post-processing scripts with a set of environment variables.
/// This type provides a single place to read the subset this tool cares about.
/// </remarks>
/// <seealso cref="Cli"/>
sealed record PrusaSlicerEnv(string OutputName, string Host)
{
    /// <summary>
    /// Attempts to read the environment values used by PrusaSlicer post-processing.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null"/> when the expected variables are missing.
    /// </remarks>
    public static PrusaSlicerEnv? TryRead()
    {
        var output = Environment.GetEnvironmentVariable("SLIC3R_PP_OUTPUT_NAME");
        var host = Environment.GetEnvironmentVariable("SLIC3R_PP_HOST");
        if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(host))
        {
            return null;
        }
        return new PrusaSlicerEnv(output, host);
    }
}
