using System;
using System.Collections.Generic;

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

enum TowerDetectionMethod
{
    None,
    TypeMarkers,
    ToolchangeBlocks,
    HeuristicWindows,
}

readonly record struct AxisAlignedBounds2D(double MinX, double MinY, double MaxX, double MaxY)
{
    public override string ToString() => $"X[{MinX:0.###},{MaxX:0.###}] Y[{MinY:0.###},{MaxY:0.###}]";
}

sealed record RawMmuSplice(
    int Index,
    int FromTool, // 0-based tool index
    int ToTool,   // 0-based tool index
    double EffectiveLocationMm,
    double EffectiveLengthMm);

sealed record RawMmuPing(
    int Index,
    double EffectiveLocationMm);

sealed record OmegaHeaderBuildInput(
    string JobName,
    string PrinterProfileHex,
    double AutoloadingOffsetMm,
    double TotalEffectivePositiveExtrusionMm,
    IReadOnlyList<string> FilamentTypes,
    IReadOnlyList<string> FilamentColorsHex,
    IReadOnlyList<int> ToolsUsed,
    IReadOnlyList<RawMmuSplice> Splices,
    IReadOnlyList<RawMmuPing> Pings,
    IReadOnlyList<OmegaAlgorithmEntry> AlgorithmTable);

sealed record OmegaAlgorithmEntry(
    int FromMaterialId,
    int ToMaterialId,
    SpliceAlgorithm Algorithm,
    string Reason);
