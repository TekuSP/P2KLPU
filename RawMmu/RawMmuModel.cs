using System;
using System.Collections.Generic;

/// <summary>
/// Result of pass-1 RAW_MMU scanning.
/// </summary>
/// <remarks>
/// This model is designed to be consumed both by analysis (<see cref="GcodeAnalyzer"/>) and by the
/// two-pass rewrite pipeline (<see cref="RawMmuTwoPassProcessor"/>).
///
/// Extrusion accounting is NET (retracts subtract, unretracts add back), matching what the Palette's
/// encoder observes; <see cref="TotalPositiveExtrusionMm"/> stays positive-only as a diagnostic.
///
/// The scan also records concrete per-line rewrite decisions (<see cref="StrippedLineIndexes"/>,
/// <see cref="ToolchangeCommandLines"/>, <see cref="PingsAfterLine"/>) so pass 2 can replay them
/// without re-implementing the state machine.
/// </remarks>
/// <seealso cref="RawMmuScanner"/>
sealed record RawMmuScanResult(
    bool ExtrusionIsAbsolute,
    double TotalPositiveExtrusionMm,
    double TotalEffectiveExtrusionMm,
    double TowerEffectiveExtrusionMm,
    double ModelEffectiveExtrusionMm,
    double IgnoredToolchangeEOnlyPositiveExtrusionMm,
    double KeptToolchangeEOnlyExtrusionMm,
    AxisAlignedBounds2D? TowerBounds,
    TowerDetectionMethod TowerDetection,
    bool SawTypeMarkers,
    bool SawExplicitToolchangeBlocks,
    bool UsedHeuristicToolchangeWindows,
    bool SawAnyToolchange,
    bool SawArcMoves,
    IReadOnlyList<int> ToolsUsed,
    IReadOnlyList<RawMmuSplice> Splices,
    IReadOnlyList<RawMmuPing> Pings,
    IReadOnlyCollection<int> StrippedLineIndexes,
    IReadOnlyDictionary<int, int> ToolchangeCommandLines,
    IReadOnlyDictionary<int, IReadOnlyList<RawMmuPing>> PingsAfterLine);
