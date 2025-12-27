/// <summary>
/// Describes how wipe tower regions were detected (if at all).
/// </summary>
/// <remarks>
/// Detection priority is marker-first:
/// <list type="number">
/// <item><description>PrusaSlicer <c>;TYPE:Wipe tower</c> / <c>;TYPE:Prime tower</c> markers</description></item>
/// <item><description>Explicit toolchange block markers (<c>; CP TOOLCHANGE START/END</c> etc.)</description></item>
/// <item><description>Heuristic windows after toolchanges (best-effort)</description></item>
/// </list>
/// </remarks>
/// <seealso cref="RawMmuScanner"/>
enum TowerDetectionMethod
{
    None,
    TypeMarkers,
    ToolchangeBlocks,
    HeuristicWindows,
}
