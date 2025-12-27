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
        var algoOverrides = new Dictionary<TransitionKey, SpliceAlgorithm>(options.AlgorithmOverrides);
        var diAlgoOverrides = new Dictionary<TransitionKey, SpliceAlgorithm>(options.DiAlgorithmOverrides);
        var materialAlgoOverrides = new Dictionary<MaterialTransitionKey, SpliceAlgorithm>(options.MaterialAlgorithmOverrides);

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

            if (key is "SPLICE_OFFSET")
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
            FilamentTypes = options.FilamentTypes,
            EmitSetActiveSpool = emitSetActiveSpool,
            RawMmuMode = rawMmuMode,
            PrinterProfileHex = printerProfile,
            AutoloadingOffsetMm = autoloadingOffset,
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
            AlgorithmOverrides = algoOverrides,
            DiAlgorithmOverrides = diAlgoOverrides,
            MaterialAlgorithmOverrides = materialAlgoOverrides
        };

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
            if (!token.StartsWith("DI", StringComparison.OrdinalIgnoreCase))
                return false;
            var num = token[2..];
            return int.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out input) && input > 0;
        }
    }
}
