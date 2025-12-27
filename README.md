# P2KLPU .NET POC

This folder contains a .NET C# proof-of-concept for a future rewrite.

Current focus:
- Drop GUI entirely.
- Primary target: **Klipper + Palette 2/2S connected mode**.
- Keep configuration **inside the G-code** via `;P2KLPU ...` comment directives (so PrusaSlicer can drive it).
- Normalize emitted pauses (`G4`) to be more Klipper-friendly and optionally hook macros around ping blocks.

## What it does today

- Accepts PrusaSlicer post-processing style invocation: `input.gcode [output.gcode]`.
- Reads G-code, detects tool changes (`T0`, `T1`, … / `ACTIVATE_EXTRUDER`), tracks extrusion, and prints a **splice plan**.
- With `--dry-run`, writes nothing (analysis only).
- **No CLI configuration flags** besides `--dry-run` and `--verbose`.
- Supports **in-file directives** anywhere in the G-code (usually in slicer start-gcode comments), for example:
	- `;P2KLPU SPLICE_OFFSET=0`
	- `;P2KLPU DEFAULT_ALGO=10,5,3`
	- `;P2KLPU ALGO 1-2=12,7,0`
	- Material-based algorithm overrides (no `=` form; legacy style):
		- `;P2KLPU MATERIAL_DEFAULT_0_0_0` (sets `DEFAULT_ALGO`)
		- `;P2KLPU MATERIAL_PETG_PLA_3_-1_-6` (applies to any transition where `filament_type` matches)
		- `;P2KLPU MATERIAL_DI1_DI2_3_-1_-6` (directly sets the algorithm for input transition DI1 → DI2)
	- `;P2KLPU SYNC_BEFORE_G4=1` (default is on)
	- `;P2KLPU G4_ZERO_TO_M400=1` (default is on)
	- `;P2KLPU REWRITE_M0_M1=1` (default is on)
	- `;P2KLPU DROP_M0_M1_AFTER_O1=1` (default is on)
	- `;P2KLPU SYNC_PING_MACRO_OVERRIDE=MyOwnMacro` (replaces the ping-block sync barrier line)
	- `;P2KLPU PING_MACRO_BEFORE=PING_BEGIN`
	- `;P2KLPU PING_MACRO_AFTER=PING_END`
	- `;P2KLPU OCTOPRINT_STRIP_O_COMMANDS=1` (advanced; see OctoPrint section)
- Normalizes `G4`:
	- Rewrites `G4 S<seconds>` to `G4 P<milliseconds>`.
	- In Klipper mode, replaces `G4 S0` / `G4 P0` with `M400` (configurable via `G4_ZERO_TO_M400`).
	- In Klipper mode, inserts `M400` before non-zero `G4` (configurable via `SYNC_BEFORE_G4`).
- Normalizes slicer pauses:
	- Rewrites `M0`/`M1` to `PAUSE` (configurable via `REWRITE_M0_M1`).
	- Drops an `M0`/`M1` immediately after an `O1 ...` line (configurable via `DROP_M0_M1_AFTER_O1`), because Klipper’s `[palette2]` `O1` handler already pauses.
- If a ping block is detected (the file contains `; --- ... INSERT PING CODE ...`), optional macros are inserted before the ping `G4` and after the `O31` line.
- In console analysis output, `O31` pings are decoded to millimeters:
	- `O31 Dxxxxxxxx` is a legacy “hex float32” encoding of the ping position in **mm** (matches the common `hexify_float` behavior).
	- `O31 L<mm> mm` is a more human-readable form seen in some Palette 3 workflows.

RAW_MMU hardening / diagnostics:
- In `RAW_MMU` mode, the analysis prints both:
	- **Total positive extrusion** (all positive E, including toolchange prime/unload/reload), and
	- **Effective positive extrusion** (positive E excluding E-only toolchange logistics).
- When PrusaSlicer `;TYPE:...` markers are present, the analysis also breaks effective extrusion into:
	- **Tower effective extrusion** (`;TYPE:Wipe tower` / `;TYPE:Prime tower`), and
	- **Model effective extrusion** (everything else).
- The scanner prefers explicit toolchange markers when present:
	- `; CP TOOLCHANGE START/END` and `; TOOLCHANGE START/END`.
	- This makes it much more resilient to PrusaSlicer wipe tower **sparse layers** and other layout changes, because we don’t rely solely on a fixed “N lines after Tn” heuristic.

## Build / run

- `dotnet build`
- Dry-run analysis:
	- `dotnet run --project .\\*.csproj --framework net10.0 -- input.gcode --dry-run`

## PrusaSlicer setup (post-processing)

PrusaSlicer runs post-processing scripts like:

`<script> <input.gcode> <output.gcode>`

So in PrusaSlicer you typically only enter the executable path; PrusaSlicer supplies the input/output file paths.

Windows examples:
- If you built the project and want to run via the .NET host:
	- `dotnet "C:\\path\\to\\tool\\bin\\Release\\net10.0\\*.dll"`
- If you published a self-contained executable:
	- `"C:\\path\\to\\your-tool.exe"`

Where to put it in PrusaSlicer:
- **Print Settings → Output options → Post-processing scripts**

Notes:
- Quote paths with spaces.
- You do not need to add placeholders for input/output; PrusaSlicer appends them.

### Do I need to configure anything else in PrusaSlicer?

Usually: no — just add the post-processing script.

You *may* need additional PrusaSlicer configuration depending on the workflow:
- **RAW_MMU auto-enable**: this POC can auto-enable `RAW_MMU` when PrusaSlicer footer contains `single_extruder_multi_material = 1` and the file contains tool changes and does not already look Omega-processed.
- **OctoPrint workflow** (see below): to allow auto-detection of “printing via host”, PrusaSlicer must be configured to print to a host so it sets `SLIC3R_PP_HOST`.

## Notes

- This is *not* a full-featured tool yet: it does not implement a full purge/tower rewrite pipeline.
- Today it’s most useful as a **Klipper compatibility pass** over connected-mode output (e.g., fixing `G4 S0` and letting you hook macros around ping blocks).

Slicer feature edge cases (why Python cared more than this POC):
- Features like **variable layer height** and **combine infill** mainly matter when a processor is doing geometry- or layer-structured rewrite (e.g., purge tower geometry / per-layer heuristics), because those slicer options change layer structure and how extrusion is distributed across layers.
- This .NET POC’s RAW_MMU pipeline is primarily **stream/timeline based**: it plans splices and pings from toolchange events plus “effective positive extrusion” (E-distance excluding E-only toolchange logistics), and otherwise tries to keep print moves intact.
- Practically: variable layer height / combined infill may change *where* a ping falls along the E timeline, but they are not expected to break processing correctness the way they can for tower-geometry rewrite.

Klipper safety:
- Palette 2/2S “Omega” uses `O..` commands (non-standard G-code). On Klipper, the built-in `[palette2]` module registers the `O0..O32` commands directly (see `klippy/extras/palette2.py`), so a file like `samples/output/example_processed.gcode` can work without defining `gcode_macro O21`, etc.

Firmware flavor:
- PrusaSlicer writes `; gcode_flavor = ...` into the generated G-code (typically in the config footer).
	- If the file is marked as `klipper`, the POC enables Klipper-specific pause/sync fixes.
	- If the file is marked as a Marlin flavor, the POC is pass-through by default (no `G4` or `M0/M1` rewrites), but explicit ping-block overrides (e.g. `;P2KLPU SYNC_PING_MACRO_OVERRIDE=...`, `;P2KLPU PING_MACRO_BEFORE=...`, `;P2KLPU PING_MACRO_AFTER=...`) are still honored.

## Supported `;P2KLPU` directives

Directives are case-insensitive and can appear anywhere in the file.

General:
- `;P2KLPU RAW_MMU=0|1`
- `;P2KLPU PRINTERPROFILE=<hex>` (Palette2 printer profile ID)
- `;P2KLPU AUTOLOADINGOFFSET=<mm>` (see note below)
- `;P2KLPU FILAMENTOVERRIDE_DI<n>=<name>` (overrides PrusaSlicer `filament_type[n-1]` for MATERIAL matching)
- `;P2KLPU FILAMENTOVERRIDE=<name>` (alias for `FILAMENTOVERRIDE_DI1`)
- `;P2KLPU EXTRAENDFILAMENT=<mm>`
- `;P2KLPU MINSTARTSPLICE=<mm>`
- `;P2KLPU MINSPLICE=<mm>`
- `;P2KLPU SPLICEOFFSET=<mm>`
- `;P2KLPU SPLICE_OFFSET=<mm>` (alias of `SPLICEOFFSET`)

About `FILAMENTOVERRIDE`:
- This changes the material name used for `MATERIAL_<FROM>_<TO>_h_c_k` matching and for Omega’s material table.
- Use it to introduce custom names like `PETG-MATTE` / `PETG2` even if PrusaSlicer’s `filament_type` is more generic.

Material aliases (Spoolman-style, recommended):
- You can attach a stable material token to the filament profile itself, so it follows whichever tool/extruder it is assigned to.
- Add `p2klpu_material` per-filament in PrusaSlicer metadata; the tool reads (first found wins): `custom_parameters_filament`, `filament_custom_variables`, `filament_notes`.
- Supported formats per filament entry:
	- JSON object (common in `custom_parameters_filament`): `{"p2klpu_material":"PETG-MATTE"}`
	- Key/value text (notes/custom variables): `p2klpu_material=PETG-MATTE`

About `AUTOLOADINGOFFSET`:
- In connected mode, Palette schedules splices/pings in terms of “mm of filament fed”.
- Some setups effectively have a fixed offset between what the Palette counts and what the printer has already consumed when printing starts (autoload / preloaded length).
- The processor uses this offset to shift Omega distances (notably `O30` splice positions, `O31` ping positions, and the total in `O1`) by the specified millimeters.

About `MINSTARTSPLICE` / `MINSPLICE`:
- These set minimum splice-length thresholds used for analysis warnings.
- When a computed splice length is below the configured minimum, the tool reports a warning in console output.

About `EXTRAENDFILAMENT`:
- This adds extra “tail” filament to the Omega `O1` total length so there is additional filament available after printing finishes.

Ping planning:
- `;P2KLPU PING_INTERVAL=<mm>`
- `;P2KLPU PING_MAX_INTERVAL=<mm>`
- `;P2KLPU PING_LENGTH_MULTIPLIER=<float>`

RAW_MMU toolchange stripping heuristics:
- `;P2KLPU MMU_TOOLCHANGE_WINDOW_LINES=<int>`
- `;P2KLPU MMU_E_ONLY_STRIP_THRESHOLD=<mm>`

Algorithm selection:
- `;P2KLPU DEFAULT_ALGO=h,c,k`
- `;P2KLPU ALGO 1-2=h,c,k` (accepts `=` or `:` between the key/value)
- `;P2KLPU MATERIAL_DEFAULT_h_c_k`
- `;P2KLPU MATERIAL_<FROM>_<TO>_h_c_k` where `<FROM>/<TO>` are either material names (from PrusaSlicer `filament_type`) or `DI1..DI4`.

Ping-block macro hooks (applies when a ping block is present or inserted):
- `;P2KLPU PING_MACRO_BEFORE=<gcode>`
- `;P2KLPU PING_MACRO_AFTER=<gcode>`
- `;P2KLPU PING_MACRO=<gcode>` (sets both before and after)
- `;P2KLPU SYNC_PING_MACRO_OVERRIDE=<gcode>` (replaces the ping-block sync line when it is a zero-length dwell)

Klipper-oriented normalization:
- `;P2KLPU SYNC_BEFORE_G4=0|1`
- `;P2KLPU G4_ZERO_TO_M400=0|1`
- `;P2KLPU REWRITE_M0_M1=0|1`
- `;P2KLPU DROP_M0_M1_AFTER_O1=0|1`

Spoolman integration:
- `;P2KLPU SPOOLMAN_SET_ACTIVE_SPOOL=0|1`
	- When enabled, the tool looks for per-filament spool IDs in PrusaSlicer metadata (`custom_parameters_filament`, `filament_custom_variables`, or `filament_notes`).
	- Supported key names inside those fields: `spoolman_id`, `spool_id`, `target_spool`.
	- When a tool change happens, it emits `SET_ACTIVE_SPOOL ID=<n>` (Klipper macro) when an ID is available for that tool.

OctoPrint/Marlin compatibility:
- `;P2KLPU OCTOPRINT_STRIP_O_COMMANDS=0|1`
	- When enabled, the processor rewrites Omega `O*` commands into comment markers (`;P2KLPU_OCTO O31 ...`) so Marlin never sees unknown `O*` commands.
	- This is intended for Marlin workflows *without* an OctoPrint Palette2 plugin.
	- If the processor detects host printing via PrusaSlicer (`SLIC3R_PP_HOST`) and the file is Marlin flavor, it assumes OctoPrint is in the loop and automatically disables this option to remain compatible with the OctoPrint Palette2 plugin (which requires real `O*` lines).


## OctoPrint (Marlin) connected-mode notes

The community OctoPrint Palette2 plugin intercepts Omega commands (`O21`, `O1`, `O31`, etc.) directly from the outgoing G-code stream.

Practical implications:
- For OctoPrint + Palette2 plugin workflows, the output must contain real `O*` lines (not commented out).
- The plugin typically suppresses `O*` lines so they are not sent to the printer, and replaces pings (`O31`) with a short dwell.

Auto-detection in this POC:
- If the slicer config indicates **Marlin** and PrusaSlicer provides `SLIC3R_PP_HOST`, the POC assumes printing via host (OctoPrint) and keeps Omega `O*` commands intact.
