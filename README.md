# P2PP .NET POC

This folder contains a .NET C# proof-of-concept for a future rewrite of P2PP.

Current focus:
- Drop GUI entirely.
- Primary target: **Klipper + Palette 2/2S connected mode**.
- Keep configuration **inside the G-code** via `;P2KLPU ...` comment directives (so PrusaSlicer can drive it).
- Normalize P2PP-emitted pauses (`G4`) to be more Klipper-friendly and optionally hook macros around ping blocks.

## What it does today

- Accepts PrusaSlicer post-processing style invocation: `input.gcode [output.gcode]`.
- Reads G-code, detects tool changes (`T0`, `T1`, … / `ACTIVATE_EXTRUDER`), tracks extrusion, and prints a **splice plan**.
- With `--dry-run`, writes nothing (analysis only).
- **No CLI configuration flags** besides `--dry-run` and `--verbose`.
- Supports **in-file directives** anywhere in the G-code (usually in slicer start-gcode comments), for example:
	- `;P2KLPU SPLICE_OFFSET=0`
	- `;P2KLPU DEFAULT_ALGO=10,5,3`
	- `;P2KLPU ALGO 1-2=12,7,0`
	- Material-based algorithm overrides (no `=` form; matches legacy P2PP style):
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
- Normalizes `G4`:
	- Rewrites `G4 S<seconds>` to `G4 P<milliseconds>`.
	- In Klipper mode, replaces `G4 S0` / `G4 P0` with `M400` (configurable via `G4_ZERO_TO_M400`).
	- In Klipper mode, inserts `M400` before non-zero `G4` (configurable via `SYNC_BEFORE_G4`).
- Normalizes slicer pauses:
	- Rewrites `M0`/`M1` to `PAUSE` (configurable via `REWRITE_M0_M1`).
	- Drops an `M0`/`M1` immediately after an `O1 ...` line (configurable via `DROP_M0_M1_AFTER_O1`), because Klipper’s `[palette2]` `O1` handler already pauses.
- If a P2PP ping block is detected (`; --- P2PP - INSERT PING CODE ...`), optional macros are inserted before the ping `G4` and after the `O31` line.
- In console analysis output, `O31` pings are decoded to millimeters:
	- `O31 Dxxxxxxxx` is P2PP’s legacy “hex float32” encoding of the ping position in **mm** (matches Python `hexify_float`).
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
	- `dotnet run --project .\\P2PP.Poc.csproj --framework net10.0 -- input.gcode --dry-run`

## Notes

- This is *not* a functional replacement for P2PP yet: it does not implement the full purge/tower rewrite pipeline.
- Today it’s most useful as a **Klipper compatibility pass** over existing P2PP output (e.g., fixing `G4 S0` and letting you hook macros around ping blocks).

Klipper safety:
- Palette 2/2S “Omega” uses `O..` commands (non-standard G-code). On Klipper, the built-in `[palette2]` module registers the `O0..O32` commands directly (see `klippy/extras/palette2.py`), so a file like `samples/output/example_processed.gcode` can work without defining `gcode_macro O21`, etc.

Firmware flavor:
- PrusaSlicer writes `; gcode_flavor = ...` into the generated G-code (typically in the config footer).
	- If the file is marked as `klipper`, the POC enables Klipper-specific pause/sync fixes.
	- If the file is marked as a Marlin flavor, the POC is pass-through by default (no `G4` or `M0/M1` rewrites), but explicit ping-block overrides (e.g. `;P2KLPU SYNC_PING_MACRO_OVERRIDE=...`, `;P2KLPU PING_MACRO_BEFORE=...`, `;P2KLPU PING_MACRO_AFTER=...`) are still honored.
