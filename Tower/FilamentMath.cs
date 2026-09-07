using System;

/// <summary>
/// Filament flow math shared by tower generation and purge guidance.
/// </summary>
/// <remarks>
/// All lengths are millimeters of 1.75mm filament unless stated otherwise.
/// </remarks>
static class FilamentMath
{
    /// <summary>Cross-section area of 1.75mm filament (π × 0.875²), mm².</summary>
    public const double FilamentAreaMm2 = 2.405;

    /// <summary>
    /// Filament length (mm) needed to extrude one printed line.
    /// </summary>
    /// <remarks>
    /// Mirrors P2PP's calculate_purge: volume = width × height × (length + height),
    /// the extra height term compensating for line ends.
    /// </remarks>
    public static double LineExtrusionMm(double moveLengthMm, double extrusionWidthMm, double layerHeightMm)
    {
        var volume = extrusionWidthMm * layerHeightMm * (Math.Abs(moveLengthMm) + layerHeightMm);
        return volume / FilamentAreaMm2;
    }

    /// <summary>Converts filament length (mm) to volume (mm³).</summary>
    public static double VolumeFromLength(double lengthMm) => lengthMm * FilamentAreaMm2;

    /// <summary>Converts volume (mm³) to filament length (mm).</summary>
    public static double LengthFromVolume(double volumeMm3) => volumeMm3 / FilamentAreaMm2;
}
