using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// Result of parsing a directive block or a whole-file directive scan.
/// </summary>
/// <remarks>
/// The primary behavior is in <see cref="ApplyTo"/>, which maps directives onto an <see cref="Options"/> instance.
/// </remarks>
/// <seealso cref="Directive"/>
/// <seealso cref="P2klpuDirectiveScanner"/>
/// <seealso cref="DirectiveBlock"/>
sealed record DirectiveParseResult(
    bool Found,
    int BeginLine,
    int EndLine,
    IReadOnlyList<Directive> Directives)
{
    /// <summary>
    /// Applies the parsed directives to an options instance.
    /// </summary>
    /// <remarks>
    /// Unknown directives are ignored by design to avoid breaking slicer output.
    /// </remarks>
    public Options ApplyTo(Options options)
    {
        var defaultAlgo = options.DefaultAlgorithm;
        var spliceOffset = options.SpliceOffsetMm;
        var rawMmuMode = options.RawMmuMode;
        var printerProfile = options.PrinterProfileHex;
        var autoloadingOffset = options.AutoloadingOffsetMm;
        var extraEndFilament = options.ExtraEndFilamentMm;
        var minStartSpliceLength = options.MinStartSpliceLengthMm;
        var minSpliceLength = options.MinSpliceLengthMm;
        var mmuToolchangeWindowLines = options.MmuToolchangeWindowLines;
        var mmuEOnlyStripThreshold = options.MmuEOnlyStripThresholdMm;
        var pingInitialInterval = options.PingInitialIntervalMm;
        var pingMaxInterval = options.PingMaxIntervalMm;
        var pingLengthMultiplier = options.PingLengthMultiplier;
        var syncBeforeG4 = options.SyncBeforeG4;
        var g4ZeroToM400 = options.G4ZeroToM400;
        var rewriteM0M1 = options.RewriteM0M1;
        var dropM0M1AfterO1 = options.DropM0M1AfterO1;
        var syncPingMacroOverride = options.SyncPingMacroOverride;
        var pingMacroBefore = options.PingMacroBefore;
        var pingMacroAfter = options.PingMacroAfter;
        var emitSetActiveSpool = options.EmitSetActiveSpool;
        var octoPrintStripOmegaCommands = options.OctoPrintStripOmegaCommands;
        var strict = options.Strict;
        var towerMode = options.TowerMode;
        var towerX = options.TowerXMm;
        var towerY = options.TowerYMm;
        var towerWidth = options.TowerWidthMm;
        var towerDepth = options.TowerDepthMm;
        var towerBrimLoops = options.TowerBrimLoops;
        var towerSpeed = options.TowerSpeedMmMin;
        var towerFirstLayerSpeed = options.TowerFirstLayerSpeedMmMin;
        var towerFirstLayerSpeedExplicit = false;
        var towerMaxFlowExplicit = false;
        var towerSustainPerimeters = options.TowerSustainPerimeters;
        var towerSustainSpacing = options.TowerSustainSpacingMm;
        var towerSustainLatticeLayers = options.TowerSustainLatticeLayers;
        var towerSustainAdaptive = options.TowerSustainAdaptive;
        var towerSustainDense = options.TowerSustainDenseMm;
        var towerSustainSpacingMax = options.TowerSustainSpacingMaxMm;
        var towerMaxFlow = options.TowerMaxFlowMm3PerSec;
        var towerSpliceDwell = options.TowerSpliceDwellMs;
        var towerExtrusionWidth = options.TowerExtrusionWidthMm;
        var purgeDefault = options.PurgeDefaultMm;
        var purgeByInput = new Dictionary<TransitionKey, double>(options.PurgeByInput);
        var purgeByMaterial = new Dictionary<MaterialTransitionKey, double>(options.PurgeByMaterial);
        var calibrateOffset = options.CalibrateOffset;
        var calibrateScale = options.CalibrateScale;
        var purgeJunctionOnTower = options.PurgeJunctionOnTower;
        var purgeIntoInfill = options.PurgeIntoInfill;
        var purgeIntoObjects = new List<string>(options.PurgeIntoObjects);
        var replaceSlicerTower = options.ReplaceSlicerTower;
        var algoOverrides = new Dictionary<TransitionKey, SpliceAlgorithm>(options.AlgorithmOverrides);
        var diAlgoOverrides = new Dictionary<TransitionKey, SpliceAlgorithm>(options.DiAlgorithmOverrides);
        var materialAlgoOverrides = new Dictionary<MaterialTransitionKey, SpliceAlgorithm>(options.MaterialAlgorithmOverrides);
        var filamentTypes = new List<string>(options.FilamentTypes);

        foreach (var d in Directives)
        {
            var key = d.Key.ToUpperInvariant();

            // MATERIAL directives allow mapping algorithms by material type (from slicer config) or directly by inputs.
            // Examples:
            //   ;P2KLPU MATERIAL_PETG_PLA_3_-1_-6
            //   ;P2KLPU MATERIAL_DI1_DI2_3_-1_-6
            // Applies to splice plan generation (and future processing stages) by setting algorithm overrides.
            if (key.StartsWith("MATERIAL_", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseMaterialAlgoDirective(d.Key, out var from, out var to, out var algo))
                {
                    if (from.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase)
                        && to.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase))
                    {
                        defaultAlgo = algo;
                        continue;
                    }

                    if (TryParseDirectInput(from, out var fromInput) && TryParseDirectInput(to, out var toInput))
                    {
                        diAlgoOverrides[new TransitionKey(fromInput, toInput)] = algo;
                    }
                    else
                    {
                        materialAlgoOverrides[new MaterialTransitionKey(from, to)] = algo;
                    }
                }

                continue;
            }

            if (key is "RAW_MMU")
            {
                if (TryParseBool(d.Value, out var b))
                    rawMmuMode = b;
                continue;
            }

            // Filament type override: changes the material name used for MATERIAL_<FROM>_<TO> matching.
            // Supported forms:
            //  ;P2KLPU FILAMENTOVERRIDE_DI1=PETG-MATTE
            //  ;P2KLPU FILAMENTOVERRIDE_1=PETG-MATTE
            //  ;P2KLPU FILAMENTOVERRIDE1=PETG-MATTE
            // The plain form ';P2KLPU FILAMENTOVERRIDE=<name>' applies to DI1.
            if (key.StartsWith("FILAMENTOVERRIDE", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseFilamentOverride(d.Key, d.Value, out var di, out var name))
                {
                    var idx = di - 1;
                    while (filamentTypes.Count <= idx)
                        filamentTypes.Add("");
                    filamentTypes[idx] = name;
                }
                continue;
            }

            if (key is "PRINTERPROFILE")
            {
                var v = d.Value.Trim();
                if (!string.IsNullOrWhiteSpace(v))
                    printerProfile = v;
                continue;
            }

            if (key is "AUTOLOADINGOFFSET")
            {
                if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm))
                    autoloadingOffset = mm;
                continue;
            }

            if (key is "EXTRAENDFILAMENT")
            {
                if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm) && mm >= 0)
                    extraEndFilament = mm;
                continue;
            }

            if (key is "MINSTARTSPLICE")
            {
                // The user's value is honored as-is; the analyzer warns when it is below the
                // Palette 2 manual minimum (85mm) instead of silently clamping.
                if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm) && mm > 0)
                    minStartSpliceLength = mm;
                continue;
            }

            if (key is "MINSPLICE")
            {
                // Honored as-is; the analyzer warns below the manual minimum (60mm).
                if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm) && mm > 0)
                    minSpliceLength = mm;
                continue;
            }

            if (key is "MMU_TOOLCHANGE_WINDOW_LINES")
            {
                if (int.TryParse(d.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0)
                    mmuToolchangeWindowLines = n;
                continue;
            }

            if (key is "MMU_E_ONLY_STRIP_THRESHOLD")
            {
                if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm))
                    mmuEOnlyStripThreshold = mm;
                continue;
            }

            // Linear (fixed) ping spacing override.
            // Matches the Python P2PP behavior:
            //   - sets the base interval
            //   - disables growth (multiplier=1)
            //   - keeps PingMaxIntervalMm as an independent cap
            // The value is honored as-is; the analyzer warns when it is suspiciously small.
            if (key is "LINEARPINGLENGTH" or "LINEAR_PING_LENGTH")
            {
                if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm) && mm > 0)
                {
                    pingInitialInterval = mm;
                    pingLengthMultiplier = 1.0;
                }
                continue;
            }

            if (key is "PING_INTERVAL")
            {
                if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm) && mm > 0)
                    pingInitialInterval = mm;
                continue;
            }

            if (key is "PING_MAX_INTERVAL")
            {
                if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm) && mm > 0)
                    pingMaxInterval = mm;
                continue;
            }

            if (key is "PING_LENGTH_MULTIPLIER")
            {
                if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) && m > 0)
                    pingLengthMultiplier = m;
                continue;
            }

            if (key is "DEFAULT_ALGO")
            {
                if (SpliceAlgorithm.TryParse(d.Value, out var a))
                    defaultAlgo = a;
                continue;
            }

            if (key is "SPLICE_OFFSET" or "SPLICEOFFSET")
            {
                if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm))
                    spliceOffset = mm;
                continue;
            }

            if (key is "SYNC_BEFORE_G4")
            {
                if (TryParseBool(d.Value, out var b))
                    syncBeforeG4 = b;
                continue;
            }

            if (key is "G4_ZERO_TO_M400")
            {
                if (TryParseBool(d.Value, out var b))
                    g4ZeroToM400 = b;
                continue;
            }

            if (key is "REWRITE_M0_M1")
            {
                if (TryParseBool(d.Value, out var b))
                    rewriteM0M1 = b;
                continue;
            }

            if (key is "DROP_M0_M1_AFTER_O1")
            {
                if (TryParseBool(d.Value, out var b))
                    dropM0M1AfterO1 = b;
                continue;
            }

            if (key is "SYNC_PING_MACRO_OVERRIDE")
            {
                var v = d.Value.Trim();
                syncPingMacroOverride = string.IsNullOrWhiteSpace(v) ? null : v;
                continue;
            }

            if (key is "PING_MACRO")
            {
                var v = d.Value.Trim();
                pingMacroBefore = string.IsNullOrWhiteSpace(v) ? null : v;
                pingMacroAfter = string.IsNullOrWhiteSpace(v) ? null : v;
                continue;
            }

            if (key is "PING_MACRO_BEFORE")
            {
                var v = d.Value.Trim();
                pingMacroBefore = string.IsNullOrWhiteSpace(v) ? null : v;
                continue;
            }

            if (key is "PING_MACRO_AFTER")
            {
                var v = d.Value.Trim();
                pingMacroAfter = string.IsNullOrWhiteSpace(v) ? null : v;
                continue;
            }

            if (key is "SPOOLMAN_SET_ACTIVE_SPOOL")
            {
                if (TryParseBool(d.Value, out var b))
                    emitSetActiveSpool = b;
                continue;
            }

            if (key is "OCTOPRINT_STRIP_O_COMMANDS")
            {
                if (TryParseBool(d.Value, out var b))
                    octoPrintStripOmegaCommands = b;
                continue;
            }

            if (key is "STRICT")
            {
                if (TryParseBool(d.Value, out var b))
                    strict = b;
                continue;
            }

            if (key is "TOWER")
            {
                if (TryParseBool(d.Value, out var b))
                    towerMode = b;
                continue;
            }

            if (key is "TOWER_X")
            {
                if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm))
                    towerX = mm;
                continue;
            }

            if (key is "TOWER_Y")
            {
                if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm))
                    towerY = mm;
                continue;
            }

            if (key is "TOWER_WIDTH")
            {
                if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm) && mm > 0)
                    towerWidth = mm;
                continue;
            }

            if (key is "TOWER_DEPTH")
            {
                if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm) && mm > 0)
                    towerDepth = mm;
                continue;
            }

            if (key is "TOWER_BRIM_LOOPS")
            {
                if (int.TryParse(d.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0)
                    towerBrimLoops = n;
                continue;
            }

            // TOWER_SPEED=PRINT takes the tower speeds and the flow cap from the sliced profile
            // (solid infill / first layer speed / max volumetric speed); an explicit number given
            // for the first-layer speed or the flow cap still wins over that.
            if (key is "TOWER_SPEED")
            {
                if (IsProfileToken(d.Value))
                {
                    towerSpeed = TowerProfileSpeeds.FromProfile;
                    if (!towerFirstLayerSpeedExplicit)
                        towerFirstLayerSpeed = TowerProfileSpeeds.FromProfile;
                    if (!towerMaxFlowExplicit)
                        towerMaxFlow = TowerProfileSpeeds.FromProfile;
                }
                else if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > 0)
                {
                    towerSpeed = f;
                }
                continue;
            }

            if (key is "TOWER_FIRST_LAYER_SPEED")
            {
                if (IsProfileToken(d.Value))
                {
                    towerFirstLayerSpeed = TowerProfileSpeeds.FromProfile;
                    towerFirstLayerSpeedExplicit = true;
                }
                else if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > 0)
                {
                    towerFirstLayerSpeed = f;
                    towerFirstLayerSpeedExplicit = true;
                }
                continue;
            }

            if (key is "TOWER_SUSTAIN_PERIMETERS")
            {
                if (int.TryParse(d.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1)
                    towerSustainPerimeters = n;
                continue;
            }

            // Spacing of the sparse internal lattice on sustaining layers (0 = walls only).
            if (key is "TOWER_SUSTAIN_SPACING")
            {
                if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm) && mm >= 0)
                    towerSustainSpacing = mm;
                continue;
            }

            // Number of sustaining layers directly below a purge layer that carry the lattice;
            // sustaining layers further down print walls only. Default: every layer with a purge
            // above it (1 = just the layer the purge lands on, 0 = never).
            if (key is "TOWER_SUSTAIN_LATTICE_LAYERS")
            {
                if (int.TryParse(d.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0)
                    towerSustainLatticeLayers = n;
                continue;
            }

            // Adaptive lattice: dense (TOWER_SUSTAIN_SPACING) within TOWER_SUSTAIN_DENSE mm of height
            // below the next purge layer, twice as coarse in the zone below that, four times beyond
            // (capped by TOWER_SUSTAIN_SPACING_MAX). TOWER_SUSTAIN_ADAPTIVE=0 keeps one spacing everywhere.
            if (key is "TOWER_SUSTAIN_ADAPTIVE")
            {
                if (TryParseBool(d.Value, out var b))
                    towerSustainAdaptive = b;
                continue;
            }

            if (key is "TOWER_SUSTAIN_DENSE")
            {
                if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm) && mm >= 0)
                    towerSustainDense = mm;
                continue;
            }

            if (key is "TOWER_SUSTAIN_SPACING_MAX")
            {
                if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm) && mm >= 0)
                    towerSustainSpacingMax = mm;
                continue;
            }

            // Volumetric cap on tower extrusion (mm³/s): keeps consumption during Palette splice
            // creation below what the buffer can cover (buffer error 121 protection). 0 disables.
            if (key is "TOWER_MAX_FLOW")
            {
                if (IsProfileToken(d.Value))
                {
                    towerMaxFlow = TowerProfileSpeeds.FromProfile;
                    towerMaxFlowExplicit = true;
                }
                else if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f >= 0)
                {
                    towerMaxFlow = f;
                    towerMaxFlowExplicit = true;
                }
                continue;
            }

            // Optional dwell (ms) at the start of each purge visit, giving the Palette a head start
            // on the upcoming splice before purge consumption begins.
            if (key is "TOWER_SPLICE_DWELL")
            {
                if (int.TryParse(d.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) && ms >= 0)
                    towerSpliceDwell = ms;
                continue;
            }

            if (key is "TOWER_EXTRUSION_WIDTH")
            {
                if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm) && mm > 0)
                    towerExtrusionWidth = mm;
                continue;
            }


            // SPLICEOFFSET calibration print: start,step,count[,toDI]
            //   ;P2KLPU CALIBRATE_OFFSET=20,20,8
            if (key is "CALIBRATE_OFFSET")
            {
                var parts = d.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 3
                    && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var start)
                    && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var step)
                    && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
                {
                    int? toInput = null;
                    var allInputs = false;
                    if (parts.Length >= 4)
                    {
                        if (parts[3].Equals("ALL", StringComparison.OrdinalIgnoreCase))
                            allInputs = true;
                        else if (int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var to) && to >= 1)
                            toInput = to;
                    }
                    calibrateOffset = new OffsetCalibration(start, step, count, toInput, allInputs);
                }
                continue;
            }

            // Direct-reading scale beside each calibration square. Off by default: the thin
            // single-layer strokes are tedious to remove from the bed.
            //   ;P2KLPU CALIBRATE_SCALE=1
            if (key is "CALIBRATE_SCALE")
            {
                if (TryParseBool(d.Value, out var b))
                    calibrateScale = b;
                continue;
            }

            if (key is "PURGE_DEFAULT")
            {
                if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm) && mm > 0)
                    purgeDefault = mm;
                continue;
            }

            // Where the color change may land when PrusaSlicer wipes into infill/object:
            //   ;P2KLPU PURGE_JUNCTION=MODEL   (default; INFILL is accepted too) - in the wiped infill when it is long enough
            //   ;P2KLPU PURGE_JUNCTION=TOWER   - always on the tower (SPLICEOFFSET + 15 mm stays there)
            // P2KLPU-native wipe into infill / object (TOWER mode, no slicer tower needed):
            //   ;P2KLPU PURGE_INTO_INFILL=1
            //   ;P2KLPU PURGE_INTO_OBJECT=<Klipper object name>   (repeatable)
            if (key is "PURGE_INTO_INFILL")
            {
                if (TryParseBool(d.Value, out var b))
                    purgeIntoInfill = b;
                continue;
            }

            if (key is "PURGE_INTO_OBJECT" or "PURGE_INTO_OBJECTS")
            {
                foreach (var name in d.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var n = name.Trim('\'', '"');
                    if (n.Length > 0 && !purgeIntoObjects.Exists(x => x.Equals(n, StringComparison.OrdinalIgnoreCase)))
                        purgeIntoObjects.Add(n);
                }
                continue;
            }

            // Allow (and remove) a PrusaSlicer wipe tower in the export instead of erroring out.
            if (key is "TOWER_REPLACE_SLICER_TOWER")
            {
                if (TryParseBool(d.Value, out var b))
                    replaceSlicerTower = b;
                continue;
            }

            if (key is "PURGE_JUNCTION")
            {
                var v = d.Value.Trim();
                if (v.Equals("TOWER", StringComparison.OrdinalIgnoreCase))
                    purgeJunctionOnTower = true;
                else if (v.Equals("MODEL", StringComparison.OrdinalIgnoreCase) || v.Equals("INFILL", StringComparison.OrdinalIgnoreCase) || v.Equals("OBJECT", StringComparison.OrdinalIgnoreCase))
                    purgeJunctionOnTower = false;
                continue;
            }

            // Per-pair purge lengths (filament mm):
            //   ;P2KLPU PURGE_PETG_PLA=120
            //   ;P2KLPU PURGE_DI1_DI2=90   (also IN1/IN2 tokens)
            if (key.StartsWith("PURGE_", StringComparison.OrdinalIgnoreCase))
            {
                var parts = d.Key.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 3
                    && double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm)
                    && mm > 0)
                {
                    if (TryParseDirectInput(parts[1], out var fromIn) && TryParseDirectInput(parts[2], out var toIn))
                        purgeByInput[new TransitionKey(fromIn, toIn)] = mm;
                    else
                        purgeByMaterial[new MaterialTransitionKey(parts[1], parts[2])] = mm;
                }
                continue;
            }

            if (key is "ALGO")
            {
                // Expected form in Value: "1-2=10,5,3" OR "1-2:10,5,3"
                var txt = d.Value.Replace('=', ':');
                if (TryParseAlgoOverride(txt, out var k, out var a))
                    algoOverrides[k] = a;
                continue;
            }

            // Unknown directives are ignored (we do not want to break slicer output).
        }

        return options with
        {
            DefaultAlgorithm = defaultAlgo,
            SpliceOffsetMm = spliceOffset,
            FilamentTypes = filamentTypes,
            EmitSetActiveSpool = emitSetActiveSpool,
            RawMmuMode = rawMmuMode,
            PrinterProfileHex = printerProfile,
            AutoloadingOffsetMm = autoloadingOffset,
            ExtraEndFilamentMm = extraEndFilament,
            MinStartSpliceLengthMm = minStartSpliceLength,
            MinSpliceLengthMm = minSpliceLength,
            MmuToolchangeWindowLines = mmuToolchangeWindowLines,
            MmuEOnlyStripThresholdMm = mmuEOnlyStripThreshold,
            PingInitialIntervalMm = pingInitialInterval,
            PingMaxIntervalMm = pingMaxInterval,
            PingLengthMultiplier = pingLengthMultiplier,
            SyncBeforeG4 = syncBeforeG4,
            G4ZeroToM400 = g4ZeroToM400,
            RewriteM0M1 = rewriteM0M1,
            DropM0M1AfterO1 = dropM0M1AfterO1,
            SyncPingMacroOverride = syncPingMacroOverride,
            PingMacroBefore = pingMacroBefore,
            PingMacroAfter = pingMacroAfter,
            OctoPrintStripOmegaCommands = octoPrintStripOmegaCommands,
            Strict = strict,
            TowerMode = towerMode,
            TowerXMm = towerX,
            TowerYMm = towerY,
            TowerWidthMm = towerWidth,
            TowerDepthMm = towerDepth,
            TowerBrimLoops = towerBrimLoops,
            TowerSpeedMmMin = towerSpeed,
            TowerFirstLayerSpeedMmMin = towerFirstLayerSpeed,
            TowerSustainPerimeters = towerSustainPerimeters,
            TowerSustainSpacingMm = towerSustainSpacing,
            TowerSustainLatticeLayers = towerSustainLatticeLayers,
            TowerSustainAdaptive = towerSustainAdaptive,
            TowerSustainDenseMm = towerSustainDense,
            TowerSustainSpacingMaxMm = towerSustainSpacingMax,
            TowerMaxFlowMm3PerSec = towerMaxFlow,
            TowerSpliceDwellMs = towerSpliceDwell,
            TowerExtrusionWidthMm = towerExtrusionWidth,
            PurgeDefaultMm = purgeDefault,
            PurgeOverridesByInput = purgeByInput,
            PurgeOverridesByMaterial = purgeByMaterial,
            CalibrateOffset = calibrateOffset,
            CalibrateScale = calibrateScale,
            PurgeJunctionOnTower = purgeJunctionOnTower,
            PurgeIntoInfill = purgeIntoInfill,
            PurgeIntoObjectNames = purgeIntoObjects,
            ReplaceSlicerTower = replaceSlicerTower,
            AlgorithmOverrides = algoOverrides,
            DiAlgorithmOverrides = diAlgoOverrides,
            MaterialAlgorithmOverrides = materialAlgoOverrides
        };

        static bool TryParseFilamentOverride(string rawKey, string rawValue, out int di, out string name)
        {
            di = 1;
            name = "";

            var key = rawKey.Trim();
            var upper = key.ToUpperInvariant();
            var value = rawValue.Trim();

            // Allow DI index to be specified inside the value for the plain key.
            // Examples:
            //  FILAMENTOVERRIDE=DI2=PETG-MATTE
            //  FILAMENTOVERRIDE=DI2:PETG-MATTE
            if (upper.Equals("FILAMENTOVERRIDE", StringComparison.OrdinalIgnoreCase)
                && (value.StartsWith("DI", StringComparison.OrdinalIgnoreCase) || value.StartsWith("di", StringComparison.OrdinalIgnoreCase)))
            {
                var v = value;
                var sep = v.IndexOf('=');
                if (sep < 0) sep = v.IndexOf(':');
                if (sep > 0)
                {
                    var left = v[..sep].Trim();
                    var right = v[(sep + 1)..].Trim();
                    if (TryParseDirectInput(left, out var input) && !string.IsNullOrWhiteSpace(right))
                    {
                        di = input;
                        name = right;
                        return true;
                    }
                }
            }

            // Key-encoded DI index.
            // FILAMENTOVERRIDE_DI2, FILAMENTOVERRIDE_2, FILAMENTOVERRIDE2
            var k = upper;

            if (k.Equals("FILAMENTOVERRIDE", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(value))
                    return false;
                di = 1;
                name = value;
                return true;
            }

            const string diPrefix = "FILAMENTOVERRIDE_DI";
            const string underscorePrefix = "FILAMENTOVERRIDE_";
            const string barePrefix = "FILAMENTOVERRIDE";

            if (k.StartsWith(diPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var num = k[diPrefix.Length..].Trim();
                if (int.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 && !string.IsNullOrWhiteSpace(value))
                {
                    di = n;
                    name = value;
                    return true;
                }
                return false;
            }

            if (k.StartsWith(underscorePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var num = k[underscorePrefix.Length..].Trim();
                if (int.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 && !string.IsNullOrWhiteSpace(value))
                {
                    di = n;
                    name = value;
                    return true;
                }
                return false;
            }

            if (k.StartsWith(barePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var num = k[barePrefix.Length..].Trim();
                if (int.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 && !string.IsNullOrWhiteSpace(value))
                {
                    di = n;
                    name = value;
                    return true;
                }
            }

            return false;
        }

        static bool IsProfileToken(string text)
        {
            var t = text.Trim();
            return t.Equals("PRINT", StringComparison.OrdinalIgnoreCase)
                || t.Equals("PROFILE", StringComparison.OrdinalIgnoreCase)
                || t.Equals("AUTO", StringComparison.OrdinalIgnoreCase);
        }

        static bool TryParseBool(string text, out bool value)
        {
            value = false;
            var t = text.Trim();
            if (t.Length == 0) return false;
            if (t.Equals("1", StringComparison.OrdinalIgnoreCase) || t.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || t.Equals("YES", StringComparison.OrdinalIgnoreCase) || t.Equals("ON", StringComparison.OrdinalIgnoreCase))
            {
                value = true;
                return true;
            }
            if (t.Equals("0", StringComparison.OrdinalIgnoreCase) || t.Equals("FALSE", StringComparison.OrdinalIgnoreCase) || t.Equals("NO", StringComparison.OrdinalIgnoreCase) || t.Equals("OFF", StringComparison.OrdinalIgnoreCase))
            {
                value = false;
                return true;
            }
            return false;
        }

        static bool TryParseAlgoOverride(string text, out TransitionKey key, out SpliceAlgorithm algo)
        {
            key = default;
            algo = default;
            var parts = text.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2) return false;
            var lr = parts[0].Split('-', 2, StringSplitOptions.TrimEntries);
            if (lr.Length != 2) return false;
            if (!int.TryParse(lr[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var from) || from < 1) return false;
            if (!int.TryParse(lr[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var to) || to < 1) return false;
            if (!SpliceAlgorithm.TryParse(parts[1], out var a)) return false;
            key = new TransitionKey(from, to);
            algo = a;
            return true;
        }

        static bool TryParseMaterialAlgoDirective(string rawKey, out string from, out string to, out SpliceAlgorithm algo)
        {
            from = "";
            to = "";
            algo = default;

            // Key is the entire directive key, e.g. "MATERIAL_PETG_PLA_3_-1_-6".
            var parts = rawKey.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 5) return false;
            if (!parts[0].Equals("MATERIAL", StringComparison.OrdinalIgnoreCase)) return false;

            // Shorthand:
            //  MATERIAL_DEFAULT_h_c_k
            if (parts.Length == 5 && parts[1].Equals("DEFAULT", StringComparison.OrdinalIgnoreCase))
            {
                from = "DEFAULT";
                to = "DEFAULT";
                if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h0)) return false;
                if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c0)) return false;
                if (!int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var k0)) return false;
                algo = new SpliceAlgorithm(h0, c0, k0);
                return true;
            }

            // Full form:
            //  MATERIAL_<FROM>_<TO>_h_c_k
            if (parts.Length < 6) return false;

            from = parts[1];
            to = parts[2];

            if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)) return false;
            if (!int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c)) return false;
            if (!int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var k)) return false;

            algo = new SpliceAlgorithm(h, c, k);
            return true;
        }

        static bool TryParseDirectInput(string token, out int input)
        {
            input = 0;

            var t = token.Trim();
            if (t.Length < 3)
                return false;

            // Accept both DI and IN prefixes:
            //  DI1, DI2, ... (preferred)
            //  IN1, IN2, ... (legacy/common shorthand)
            string num;
            if (t.StartsWith("DI", StringComparison.OrdinalIgnoreCase))
                num = t[2..];
            else if (t.StartsWith("IN", StringComparison.OrdinalIgnoreCase))
                num = t[2..];
            else
                return false;

            return int.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out input) && input > 0;
        }
    }
}
