/// <summary>
/// Printer state captured at a toolchange command, used by the custom tower generator to plan
/// a purge visit and return the print head to where it was.
/// </summary>
/// <seealso cref="RawMmuScanResult"/>
/// <seealso cref="CustomTowerGenerator"/>
sealed record ToolchangeContext(
    int LineIndex,
    int FromTool,          // -1 for the initial tool selection
    int ToTool,
    int LayerIndex,        // -1 when no layer marker was seen yet (start G-code)
    double? ResumeXmm,     // last known head position before the toolchange
    double? ResumeYmm,
    double? ResumeZmm,
    double? LastFeedrate,  // last F seen on a move
    double RetractDepthMm, // net retraction at this point (0 = primed)
    double EffectiveEMm,   // effective extrusion position at the toolchange (this scan's timeline)
    double WipedIntoModelMm = 0); // filament PrusaSlicer purged into the model right after this change (wipe into infill/object; up to its "; PURGING FINISHED" marker)
