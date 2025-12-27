/// <summary>
/// Firmware dialect determined from slicer metadata (e.g. PrusaSlicer <c>gcode_flavor</c>).
/// </summary>
/// <remarks>
/// This is used for conservative rewrite decisions (e.g. pause behavior), not for forcing toolchange style.
/// </remarks>
/// <seealso cref="FirmwareFlavorDetector"/>
enum FirmwareFlavor
{
    Klipper,
    Marlin
}
