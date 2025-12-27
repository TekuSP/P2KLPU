using System;

/// <summary>
/// Classifies PrusaSlicer/Slic3r <c>;TYPE:</c> regions relevant to wipe/prime tower detection.
/// </summary>
/// <remarks>
/// This is used by <see cref="RawMmuScanner"/> to attribute extrusion to wipe tower vs model when
/// Prusa-style type markers are present.
/// </remarks>
/// <seealso cref="RawMmuScanner"/>
internal enum PrusaType
{
    Other,
    WipeTower,
    PrimeTower,
}
