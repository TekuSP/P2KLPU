using System;

/// <summary>
/// Detects firmware dialect from PrusaSlicer embedded <c>gcode_flavor</c> comments.
/// </summary>
/// <remarks>
/// This tool follows the slicer flavor rather than allowing a directive to force Klipper/Marlin.
/// </remarks>
/// <seealso cref="FirmwareFlavor"/>
static class FirmwareFlavorDetector
{
    /// <summary>
    /// Detects the firmware flavor from PrusaSlicer embedded <c>gcode_flavor</c> comments.
    /// </summary>
    /// <remarks>
    /// This is the only supported source of truth for firmware selection (no directive-based forcing).
    /// When the flavor is missing, the detector returns a conservative default.
    /// </remarks>
    /// <param name="lines">Input G-code lines.</param>
    /// <returns>The detected firmware flavor.</returns>
    public static FirmwareFlavor Detect(string[] lines)
    {
        // PrusaSlicer typically writes this in the config footer:
        //   ; gcode_flavor = klipper
        //   ; gcode_flavor = marlin2
        // but we scan the whole file defensively.
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length < 5) continue;
            if (!line.StartsWith(";", StringComparison.Ordinal)) continue;

            // Normalize whitespace.
            var body = line[1..].Trim();
            if (!body.StartsWith("gcode_flavor", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = body.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
                continue;
            var flavor = parts[1].Trim().ToLowerInvariant();

            if (flavor.Contains("klipper", StringComparison.OrdinalIgnoreCase))
                return FirmwareFlavor.Klipper;

            // PrusaSlicer values often include: marlin, marlin2, reprap, smoothie, etc.
            // We treat non-klipper flavors as Marlin-ish for the purposes of pause rewriting.
            return FirmwareFlavor.Marlin;
        }

        // If unspecified, keep behavior conservative: assume Klipper (most sensitive).
        return FirmwareFlavor.Klipper;
    }
}
