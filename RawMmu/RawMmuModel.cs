using System;
using System.Collections.Generic;

/// <summary>
/// Result of pass-1 RAW_MMU scanning.
/// </summary>
/// <remarks>
/// This model is designed to be consumed both by analysis (<see cref="GcodeAnalyzer"/>) and by the
/// two-pass rewrite pipeline (<see cref="RawMmuTwoPassProcessor"/>).
/// </remarks>
/// <seealso cref="RawMmuScanner"/>
sealed record RawMmuScanResult(
    bool ExtrusionIsAbsolute,
    double TotalPositiveExtrusionMm,
    double TotalEffectivePositiveExtrusionMm,
    double TowerEffectivePositiveExtrusionMm,
    double ModelEffectivePositiveExtrusionMm,
    double IgnoredToolchangeEOnlyPositiveExtrusionMm,
    AxisAlignedBounds2D? TowerBounds,
    TowerDetectionMethod TowerDetection,
    bool SawTypeMarkers,
    bool SawExplicitToolchangeBlocks,
    bool UsedHeuristicToolchangeWindows,
    bool SawAnyToolchange,
    IReadOnlyList<int> ToolsUsed,
    IReadOnlyList<RawMmuSplice> Splices,
    IReadOnlyList<RawMmuPing> Pings);
