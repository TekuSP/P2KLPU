using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

/// <summary>
/// A block of the model's own extrusion (a layer's internal infill, or everything of a sacrificial
/// object) that is printed right after a toolchange as purge instead of at its sliced position.
/// </summary>
/// <param name="RemovedLines">Input lines dropped at the original position: the block body plus the travel run that led into it.</param>
/// <param name="BodyFromLine">First line copied into the purge visit (the <c>;TYPE</c> marker, or <c>EXCLUDE_OBJECT_START</c>).</param>
/// <param name="BodyToLine">Last line copied (the block's last extrusion, or <c>EXCLUDE_OBJECT_END</c>).</param>
/// <param name="ObjectToken">The object's NAME token as the slicer wrote it (may be quoted); empty when the export carries no object labels.</param>
/// <param name="WrapObject">True when the copied lines need <c>EXCLUDE_OBJECT_START/END</c> around them (infill blocks).</param>
/// <param name="IsInfill">Internal infill (true) or a sacrificial object (false).</param>
/// <param name="EntryX">Head position the block expects at its first extrusion.</param>
/// <param name="EntryY">Head position the block expects at its first extrusion.</param>
/// <param name="ExitX">Head position after the block.</param>
/// <param name="ExitY">Head position after the block.</param>
/// <param name="NetEMm">Filament the copied lines deposit (extrusion moves only; retracts and unretracts excluded).</param>
/// <param name="AccelCommand">The M204 PrusaSlicer wrote for the block ahead of its marker, replayed before the copied lines.</param>
sealed record PurgeIntoModelBlock(
    IReadOnlyList<int> RemovedLines,
    int BodyFromLine,
    int BodyToLine,
    string ObjectToken,
    bool WrapObject,
    bool IsInfill,
    double EntryX,
    double EntryY,
    double ExitX,
    double ExitY,
    double NetEMm,
    string? AccelCommand = null);

/// <summary>
/// P2KLPU-native wipe into infill / wipe into object: finds, for every tool transition, the blocks of
/// the model that follow it on the same layer and whose color does not matter — internal infill
/// (<c>;TYPE:Internal infill</c>) and, when requested, whole sacrificial objects — and picks them in
/// stream order until their filament covers the transition's pair purge. Those blocks are then
/// printed inside the purge visit and dropped from their sliced position, so the tower only takes
/// what the model cannot. Perimeters and solid/top infill are never touched.
/// </summary>
/// <remarks>
/// Works on PrusaSlicer's own labels: every feature starts with a <c>;TYPE:</c> marker, objects are
/// wrapped in <c>EXCLUDE_OBJECT_START/END</c>, layers begin with <c>;LAYER_CHANGE</c>. A block ends at
/// the next feature marker, object marker, layer marker or toolchange; the retract/travel/unretract
/// run that led into it goes with it. No slicer wipe tower is involved.
/// </remarks>
/// <seealso cref="PurgeIntoModelEmitter"/>
/// <seealso cref="PurgeDemandPlanner"/>
static class PurgeIntoModelScanner
{
    /// <summary>Blocks that deposit less than this stay where they are: the travel, prime and retract of a visit cost more than they purge.</summary>
    public const double MinBlockMm = 0.5;

    public static IReadOnlyDictionary<int, IReadOnlyList<PurgeIntoModelBlock>> Collect(string[] lines, RawMmuScanResult scan, Options options)
    {
        var result = new Dictionary<int, IReadOnlyList<PurgeIntoModelBlock>>();
        if (!options.PurgeIntoInfill && options.PurgeIntoObjects.Count == 0)
            return result;

        var sacrificial = new HashSet<string>(options.PurgeIntoObjects.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        var toolchangeLines = scan.ToolchangeCommandLines.Keys.OrderBy(k => k).ToList();

        foreach (var change in scan.ToolchangeContexts.Where(c => c.FromTool >= 0 && c.ToTool != c.FromTool).OrderBy(c => c.LineIndex))
        {
            var windowEnd = lines.Length;
            foreach (var t in toolchangeLines)
            {
                if (t > change.LineIndex)
                {
                    windowEnd = t;
                    break;
                }
            }

            var target = PurgeDemandPlanner.ResolvePairPurge(options, change.FromTool, change.ToTool);
            var blocks = new List<PurgeIntoModelBlock>();
            var taken = 0.0;
            var x = change.ResumeXmm ?? 0.0;
            var y = change.ResumeYmm ?? 0.0;
            var objectToken = "";

            var i = change.LineIndex + 1;
            while (i < windowEnd && taken < target - 0.01)
            {
                var raw = lines[i];
                if (string.IsNullOrWhiteSpace(raw))
                {
                    i++;
                    continue;
                }

                var trimmed = raw.Trim();
                if (trimmed.StartsWith(";", StringComparison.Ordinal))
                {
                    if (trimmed.Equals(";LAYER_CHANGE", StringComparison.OrdinalIgnoreCase))
                        break;
                    if (options.PurgeIntoInfill && IsTypeMarker(trimmed, "Internal infill"))
                    {
                        var block = BuildInfillBlock(lines, i, windowEnd, change.LineIndex, x, y, objectToken);
                        if (block is not null)
                        {
                            if (block.NetEMm >= MinBlockMm)
                            {
                                blocks.Add(block);
                                taken += block.NetEMm;
                            }
                            x = block.ExitX;
                            y = block.ExitY;
                            i = block.BodyToLine + 1;
                            continue;
                        }
                    }
                    i++;
                    continue;
                }

                var code = RawMmuScanner.StripComment(raw);
                if (code.StartsWith("EXCLUDE_OBJECT_START", StringComparison.OrdinalIgnoreCase))
                {
                    objectToken = ObjectTokenOf(code);
                    if (sacrificial.Contains(Normalize(objectToken)))
                    {
                        var block = BuildObjectBlock(lines, i, windowEnd, change.LineIndex, x, y, objectToken);
                        if (block is not null)
                        {
                            if (block.NetEMm >= MinBlockMm)
                            {
                                blocks.Add(block);
                                taken += block.NetEMm;
                            }
                            x = block.ExitX;
                            y = block.ExitY;
                            i = block.BodyToLine + 1;
                            objectToken = "";
                            continue;
                        }
                    }
                }
                else if (code.StartsWith("EXCLUDE_OBJECT_END", StringComparison.OrdinalIgnoreCase))
                {
                    objectToken = "";
                }
                else if (RawMmuScanner.IsExtrusionMoveCommand(code, out _))
                {
                    if (RawMmuScanner.TryGetParam(code, 'X', out var nx)) x = nx;
                    if (RawMmuScanner.TryGetParam(code, 'Y', out var ny)) y = ny;
                }

                i++;
            }

            if (blocks.Count > 0)
                result[change.LineIndex] = blocks;
        }

        return result;
    }

    /// <summary>An internal-infill block: from its ;TYPE marker to its last extrusion before the next feature.</summary>
    private static PurgeIntoModelBlock? BuildInfillBlock(string[] lines, int markerIndex, int windowEnd, int toolchangeLine, double entryX, double entryY, string objectToken)
    {
        var x = entryX;
        var y = entryY;
        var lastExtrusion = -1;
        var exitX = entryX;
        var exitY = entryY;

        for (var j = markerIndex + 1; j < windowEnd; j++)
        {
            var raw = lines[j];
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var trimmed = raw.Trim();
            if (trimmed.StartsWith(";", StringComparison.Ordinal))
            {
                if (IsAnnotationComment(trimmed))
                    continue;
                break;   // next feature marker, layer marker, or anything else structural
            }

            var code = RawMmuScanner.StripComment(raw);
            if (code.Length == 0)
                continue;
            if (code.StartsWith("EXCLUDE_OBJECT_", StringComparison.OrdinalIgnoreCase)
                || RawMmuScanner.TryParseToolChange(code, out _)
                || code.StartsWith("ACTIVATE_EXTRUDER", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (RawMmuScanner.IsExtrusionMoveCommand(code, out var isArc))
            {
                if (RawMmuScanner.TryGetParam(code, 'X', out var nx)) x = nx;
                if (RawMmuScanner.TryGetParam(code, 'Y', out var ny)) y = ny;
                var hasE = RawMmuScanner.TryGetParam(code, 'E', out var e);
                if (hasE && e > 0 && (isArc || RawMmuScanner.HasParam(code, 'X') || RawMmuScanner.HasParam(code, 'Y')))
                {
                    lastExtrusion = j;
                    exitX = x;
                    exitY = y;
                }
            }
        }

        if (lastExtrusion < 0)
            return null;

        var removed = new List<int>();
        for (var k = markerIndex; k <= lastExtrusion; k++)
            removed.Add(k);
        var accel = RemoveLeadRunAndBalance(lines, markerIndex, lastExtrusion, toolchangeLine, removed);

        return new PurgeIntoModelBlock(
            removed, markerIndex, lastExtrusion, objectToken,
            WrapObject: objectToken.Length > 0, IsInfill: true,
            entryX, entryY, exitX, exitY, NetE(lines, markerIndex, lastExtrusion), accel);
    }

    /// <summary>A sacrificial object's pass: from EXCLUDE_OBJECT_START to its EXCLUDE_OBJECT_END.</summary>
    private static PurgeIntoModelBlock? BuildObjectBlock(string[] lines, int startIndex, int windowEnd, int toolchangeLine, double entryX, double entryY, string objectToken)
    {
        var x = entryX;
        var y = entryY;
        var sawExtrusion = false;
        var endIndex = -1;

        for (var j = startIndex + 1; j < windowEnd; j++)
        {
            var raw = lines[j];
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var trimmed = raw.Trim();
            if (trimmed.StartsWith(";", StringComparison.Ordinal))
            {
                if (trimmed.Equals(";LAYER_CHANGE", StringComparison.OrdinalIgnoreCase))
                    return null;
                continue;
            }

            var code = RawMmuScanner.StripComment(raw);
            if (code.Length == 0)
                continue;
            if (code.StartsWith("EXCLUDE_OBJECT_END", StringComparison.OrdinalIgnoreCase))
            {
                endIndex = j;
                break;
            }
            if (code.StartsWith("EXCLUDE_OBJECT_START", StringComparison.OrdinalIgnoreCase)
                || RawMmuScanner.TryParseToolChange(code, out _)
                || code.StartsWith("ACTIVATE_EXTRUDER", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (RawMmuScanner.IsExtrusionMoveCommand(code, out var isArc))
            {
                if (RawMmuScanner.TryGetParam(code, 'X', out var nx)) x = nx;
                if (RawMmuScanner.TryGetParam(code, 'Y', out var ny)) y = ny;
                if (RawMmuScanner.TryGetParam(code, 'E', out var e) && e > 0 && (isArc || RawMmuScanner.HasParam(code, 'X') || RawMmuScanner.HasParam(code, 'Y')))
                    sawExtrusion = true;
            }
        }

        if (endIndex < 0 || !sawExtrusion)
            return null;

        var removed = new List<int>();
        for (var k = startIndex; k <= endIndex; k++)
            removed.Add(k);
        var accel = RemoveLeadRunAndBalance(lines, startIndex, endIndex, toolchangeLine, removed);

        return new PurgeIntoModelBlock(
            removed, startIndex, endIndex, objectToken,
            WrapObject: false, IsInfill: false,
            entryX, entryY, x, y, NetE(lines, startIndex, endIndex), accel);
    }

    /// <summary>
    /// Drops the retract / hop / travel / unretract run that led into the block and keeps the
    /// extruder's retract state around the hole consistent.
    /// </summary>
    /// <remarks>
    /// The run's travel and Z moves always go. Accel, fan and object-label lines inside the run stay
    /// where they are (the feature after the hole may rely on them) and do not end the run. Its
    /// retract/unretract lines and the E-only lines of the block body are removed only as far as
    /// the removed span nets to zero: when it does not (PrusaSlicer wrote the block's closing
    /// retract inside the object label while the matching unretract sits in the run), the earliest
    /// lines of the run that carry the remainder stay in place; failing that, the last E-only lines
    /// of the body stay (the visit prints them as well and restores the state on its own); failing
    /// that, every E-only line of the span stays. Returns the run's last M204 so the visit can
    /// replay the block's acceleration.
    /// </remarks>
    private static string? RemoveLeadRunAndBalance(string[] lines, int bodyStart, int bodyEnd, int toolchangeLine, List<int> removed)
    {
        var bodyE = new List<(int Index, double E)>();
        for (var k = bodyStart; k <= bodyEnd; k++)
        {
            var code = RawMmuScanner.StripComment(lines[k]);
            if (code.Length > 0 && RawMmuScanner.IsExtrusionMoveCommand(code, out _) && RawMmuScanner.IsEOnlyMove(code)
                && RawMmuScanner.TryGetParam(code, 'E', out var e))
                bodyE.Add((k, e));
        }

        var travelLines = new List<int>();
        var leadE = new List<(int Index, double E)>();   // nearest first
        string? accel = null;

        for (var k = bodyStart - 1; k > toolchangeLine; k--)
        {
            var raw = lines[k];
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var trimmed = raw.Trim();
            if (trimmed.StartsWith(";", StringComparison.Ordinal))
            {
                if (IsAnnotationComment(trimmed))
                {
                    travelLines.Add(k);
                    continue;
                }
                break;
            }

            var code = RawMmuScanner.StripComment(raw);
            if (code.Length == 0)
                continue;
            if (IsRunPassThrough(code))
            {
                if (accel is null && code.StartsWith("M204", StringComparison.OrdinalIgnoreCase))
                    accel = code;
                continue;
            }
            if (RawMmuScanner.IsExtrusionMoveCommand(code, out var isArc))
            {
                var hasE = RawMmuScanner.TryGetParam(code, 'E', out var e);
                var hasXY = RawMmuScanner.HasParam(code, 'X') || RawMmuScanner.HasParam(code, 'Y');
                if (isArc || (hasE && e > 0 && hasXY))
                    break;   // the previous feature's last extrusion
                if (hasE)
                    leadE.Add((k, e));
                else
                    travelLines.Add(k);
                continue;
            }
            break;
        }

        removed.AddRange(travelLines);
        leadE.Reverse();   // file order

        var span = leadE.Sum(x => x.E) + bodyE.Sum(x => x.E);
        if (Math.Abs(span) <= 0.01)
        {
            removed.AddRange(leadE.Select(x => x.Index));
            return accel;
        }

        // Keep the shortest prefix of the run's E-only lines that carries the remainder.
        var acc = 0.0;
        for (var n = 0; n < leadE.Count; n++)
        {
            acc += leadE[n].E;
            if (Math.Abs(acc - span) <= 0.01)
            {
                removed.AddRange(leadE.Skip(n + 1).Select(x => x.Index));
                return accel;
            }
        }

        // Otherwise the shortest suffix of the body's E-only lines stays in place.
        acc = 0.0;
        for (var n = bodyE.Count - 1; n >= 0; n--)
        {
            acc += bodyE[n].E;
            if (Math.Abs(acc - span) <= 0.01)
            {
                foreach (var x in bodyE.Skip(n))
                    removed.Remove(x.Index);
                removed.AddRange(leadE.Select(x => x.Index));
                return accel;
            }
        }

        // Last resort: every retract/unretract of the span stays where it is.
        foreach (var x in bodyE)
            removed.Remove(x.Index);
        return accel;
    }

    /// <summary>Lines that may sit inside a travel run without belonging to it: they stay in place and do not end the run.</summary>
    private static bool IsRunPassThrough(string code)
        => code.StartsWith("EXCLUDE_OBJECT_START", StringComparison.OrdinalIgnoreCase)
           || code.StartsWith("EXCLUDE_OBJECT_END", StringComparison.OrdinalIgnoreCase)
           || code.StartsWith("M204", StringComparison.OrdinalIgnoreCase)
           || code.StartsWith("M400", StringComparison.OrdinalIgnoreCase)
           || code.StartsWith("M106", StringComparison.OrdinalIgnoreCase)
           || code.StartsWith("M107", StringComparison.OrdinalIgnoreCase)
           || code.StartsWith("M900", StringComparison.OrdinalIgnoreCase)
           || code.StartsWith("SET_PRESSURE_ADVANCE", StringComparison.OrdinalIgnoreCase)
           || code.StartsWith("SET_VELOCITY_LIMIT", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Filament the block deposits: extrusion moves only. Retract/unretract pairs inside the block
    /// (and a sacrificial object's closing retract) net to zero once the emitter restores the
    /// extruder state, so they must not shrink the purge the model is credited with.
    /// </summary>
    private static double NetE(string[] lines, int from, int to)
    {
        var e = 0.0;
        for (var k = from; k <= to; k++)
        {
            var code = RawMmuScanner.StripComment(lines[k]);
            if (code.Length == 0 || !RawMmuScanner.IsExtrusionMoveCommand(code, out var isArc))
                continue;
            if (!RawMmuScanner.TryGetParam(code, 'E', out var v) || v <= 0)
                continue;
            if (isArc || RawMmuScanner.HasParam(code, 'X') || RawMmuScanner.HasParam(code, 'Y'))
                e += v;
        }
        return e;
    }

    private static bool IsTypeMarker(string trimmedComment, string type)
    {
        var rest = trimmedComment[1..].Trim();
        return rest.StartsWith("TYPE:", StringComparison.OrdinalIgnoreCase)
               && rest[5..].Trim().Equals(type, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAnnotationComment(string trimmedComment)
        => trimmedComment.StartsWith(";WIDTH:", StringComparison.OrdinalIgnoreCase)
           || trimmedComment.StartsWith(";HEIGHT:", StringComparison.OrdinalIgnoreCase)
           || trimmedComment.StartsWith(";--", StringComparison.Ordinal);

    private static string ObjectTokenOf(string code)
    {
        var idx = code.IndexOf("NAME=", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return "";
        var rest = code[(idx + 5)..].Trim();
        var end = rest.IndexOf(' ');
        return end < 0 ? rest : rest[..end];
    }

    private static string Normalize(string token) => token.Trim().Trim('\'', '"');
}

/// <summary>
/// Emits relocated model blocks inside a purge visit: travel to the block, prime, the block's own
/// lines verbatim (wrapped in its Klipper object label), retract, and finally the return to where
/// the toolchange left the head, with the extruder's retract state restored.
/// </summary>
/// <seealso cref="PurgeIntoModelScanner"/>
/// <seealso cref="TowerVisitEmitter"/>
static class PurgeIntoModelEmitter
{
    private const double HopMm = 0.6;
    private const string TravelF = "8640";
    private const string ZTravelF = "10800";

    /// <summary>Appends the relocation G-code and returns the filament it extrudes (net, all lines).</summary>
    /// <remarks><paramref name="startLifted"/>: the head already sits at the hop height (a tower visit just left it there), so no initial hop is emitted.</remarks>
    public static double Append(
        List<string> gcode,
        string[] lines,
        IReadOnlyList<PurgeIntoModelBlock> blocks,
        double layerZ,
        double startDepthMm,
        double retractLenMm,
        double retractF,
        double deretractF,
        double? resumeX,
        double? resumeY,
        double? resumeZ,
        double entryDepthMm,
        double? lastFeedrate,
        double speedMmMin,
        bool startLifted = false)
    {
        var depth = startDepthMm;
        var printed = 0.0;

        void Add(string line)
        {
            gcode.Add(line);
            var code = RawMmuScanner.StripComment(line);
            if (code.Length > 0 && RawMmuScanner.IsExtrusionMoveCommand(code, out _) && RawMmuScanner.TryGetParam(code, 'E', out var e))
            {
                printed += e;
                if (RawMmuScanner.IsEOnlyMove(code))
                    depth -= e;   // negative = over-primed; the closing retract/restore below undoes it
            }
        }

        if (depth < retractLenMm - 0.01)
            Add($"G1 E-{F(retractLenMm - depth)} F{F0(retractF)}");
        if (!startLifted)
            Add($"G1 Z{F(layerZ + HopMm)} F{ZTravelF}");

        foreach (var b in blocks)
        {
            Add($"; --- P2KLPU purge into {(b.IsInfill ? "infill" : "object")}{(b.ObjectToken.Length > 0 ? " of " + b.ObjectToken : "")}: {F(b.NetEMm)}mm of the model printed as purge");
            Add($"G1 X{F(b.EntryX)} Y{F(b.EntryY)} F{TravelF}");
            Add($"G1 Z{F(layerZ)} F{ZTravelF}");
            var prime = depth - LeadingUnretractMm(lines, b);   // a body that primes itself keeps its own unretract
            if (prime > 0.01)
                Add($"G1 E{F(prime)} F{F0(deretractF)} ; prime");
            Add($"G1 F{F0(speedMmMin)}");
            if (b.AccelCommand is not null)
                Add(b.AccelCommand);
            if (b.WrapObject)
                Add($"EXCLUDE_OBJECT_START NAME={b.ObjectToken}");
            for (var k = b.BodyFromLine; k <= b.BodyToLine; k++)
                Add(lines[k]);
            if (b.WrapObject)
                Add($"EXCLUDE_OBJECT_END NAME={b.ObjectToken}");
            if (depth < retractLenMm - 0.01)
                Add($"G1 E-{F(retractLenMm - depth)} F{F0(retractF)}");
            Add($"G1 Z{F(layerZ + HopMm)} F{ZTravelF}");
        }

        if (resumeX.HasValue && resumeY.HasValue)
            Add($"G1 X{F(resumeX.Value)} Y{F(resumeY.Value)} F{TravelF}");
        if (resumeZ.HasValue)
            Add($"G1 Z{F(resumeZ.Value)} F{ZTravelF}");

        var toRestore = depth - entryDepthMm;
        if (toRestore > 0.01)
            Add($"G1 E{F(toRestore)} F{F0(deretractF)}");
        else if (toRestore < -0.01)
            Add($"G1 E-{F(-toRestore)} F{F0(retractF)}");

        if (lastFeedrate.HasValue)
            Add($"G1 F{F0(lastFeedrate.Value)}");

        return printed;
    }

    /// <summary>The unretract a copied body performs before its first extrusion (0 when it expects a primed extruder).</summary>
    private static double LeadingUnretractMm(string[] lines, PurgeIntoModelBlock b)
    {
        for (var k = b.BodyFromLine; k <= b.BodyToLine; k++)
        {
            var code = RawMmuScanner.StripComment(lines[k]);
            if (code.Length == 0 || !RawMmuScanner.IsExtrusionMoveCommand(code, out var isArc))
                continue;
            if (!RawMmuScanner.TryGetParam(code, 'E', out var e))
                continue;
            if (RawMmuScanner.IsEOnlyMove(code))
                return e > 0 ? e : 0;
            if (isArc || RawMmuScanner.HasParam(code, 'X') || RawMmuScanner.HasParam(code, 'Y'))
                return 0;
        }
        return 0;
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string F0(double v) => v.ToString("0", CultureInfo.InvariantCulture);
}
