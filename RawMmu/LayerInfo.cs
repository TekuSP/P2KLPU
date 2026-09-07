/// <summary>
/// One print layer discovered during scanning.
/// </summary>
/// <remarks>
/// Layers are detected from PrusaSlicer <c>;LAYER_CHANGE</c> markers (completed by the following
/// <c>;Z:</c> comment or Z move). <see cref="HeightMm"/> comes from Z deltas, so variable layer
/// height is handled naturally.
/// </remarks>
/// <seealso cref="RawMmuScanResult"/>
sealed record LayerInfo(
    int Index,                    // 0-based layer index
    double Z,                     // top-of-layer Z in mm
    double HeightMm,              // this layer's height (Z delta)
    int MarkerLineIndex,          // line index of the ;LAYER_CHANGE marker
    double RetractDepthAtMarkerMm); // net retraction when the marker was reached
