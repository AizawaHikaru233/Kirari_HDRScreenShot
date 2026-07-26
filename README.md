# Kirari（キラリ）

Windows Graphics Capture HDR screenshot utility.  It captures a display or window as `R16G16B16A16_FLOAT` (linear scRGB) and saves it as an HDR image.  The executable is `Kirari.exe`.

## Licensing

This project is released under the **GPL-3.0** license (see [LICENSE](LICENSE)).  All code in this repository is original.  Every dependency is open source (MIT / Apache-2.0 — see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)), all one-way compatible with GPL-3.0.  The PNG encoder, PNG chunk containers and the ICC profile generator are in-house implementations; JPEG encoding uses the OS-provided WIC codec (system-library exception).  No closed-source components are bundled.

## Outputs

| Format | Files | HDR mechanism | SDR preview |
| --- | --- | --- | --- |
| HDR PNG (default) | single `.png` | 16-bit Rec.2020 PQ pixels with `cICP`, `cLLI`, `mDCV`, `iCCP`, `cHRM`, `sBIT` signaling (ReShade/SpecialK style) | viewers tone-map the file themselves: HDR-aware ones via `cICP`, color-managed SDR ones via the embedded ICC profile, guided by the `cLLI`/`mDCV` light levels |
| JPEG | `.jpg` + `.hdr` | MPF secondary grayscale JPEG gain map and Adobe `hdrgm` XMP metadata | the SDR base JPEG itself |
| PNG + gain map | `.png` + `.hdr` | XMP `iTXt` plus an ancillary `gMAP` chunk containing metadata and a grayscale PNG gain map | the SDR base PNG itself |

The default HDR PNG is a single self-contained file.  Its `cLLI` MaxCLL is the 99.99th-percentile frame peak rather than the absolute maximum — Chromium scales its SDR tone map by MaxCLL, and a lone specular pixel would otherwise dim the whole image (the same approach ledoge/Special K use).  `mDCV` carries the capture monitor's real color primaries and luminance range queried through DXGI (`IDXGIOutput6`); when that metadata is unavailable (for example captures made through the system picker, which does not reveal the source monitor), it falls back to Rec.2020 primaries with a content-derived peak and zero minimum luminance.

The `iCCP` chunk contains a generated ICC v4 profile whose tone curve decodes PQ and clips at the capture monitor's SDR white level, so color-managed SDR viewers that ignore `cICP` reproduce the overlay's SDR-referenced look.  Per the PNG spec, `cICP` outranks `iCCP`, so HDR-aware viewers are unaffected.  Compatibility notes (verified against ReShade/Special K sources, 2026-07): Chromium-family viewers (Chrome, Edge, Discord/Electron) honor `cICP` and need `iCCP` present for Discord HDR; `sRGB`/`gAMA` chunks must never be written (they break Discord/SKIV detection and are stripped here); Windows Photos/Explorer `cICP` support is unverified — expect the `iCCP` fallback there.  A previously saved capture can be re-signed in place with `HdrCapture.exe --resign <file.png>` (writes `<file>_fixed.png`).

The JPEG layout follows the common Ultra HDR packaging model: an SDR primary JPEG, a secondary gain-map JPEG, MPF image index, and gain-map XMP fields.  It should be tested against the exact target decoder (Android Gallery, Chrome, libultrahdr, etc.).

PNG has no broadly deployed, cross-vendor equivalent of JPEG Ultra HDR's MPF layout.  `gMAP` is intentionally an ancillary custom chunk: ordinary PNG readers display the SDR base correctly, while this tool (or a future decoder) can recover the embedded gain map.

## Build and run

Requires the Windows 10 SDK 10.0.26100 and .NET 10 SDK.

```powershell
dotnet build .\HdrCapture.csproj
dotnet run --project .\HdrCapture.csproj
```

The application starts silently in the notification area; it does not open a main window.

## Capturing

Press the capture hotkey (default `Ctrl+Shift+A`) or double-click the tray icon. This freezes the monitor under the cursor as a full-precision HDR frame and shows a PixPin-style overlay:

- **Move the mouse** to auto-highlight the window under the cursor.
- **Click** to capture the highlighted window; **drag** to select an arbitrary region.
- A magnifier loupe follows the cursor with a pixel grid, the cursor coordinates and the color
  under the cursor (from the SDR-referenced view); **C** copies the color value, **Shift**
  toggles between RGB and hex.
- **Esc** or **right-click** to cancel.

After the selection an annotation toolbar appears: pen, arrow, shape (rectangle/ellipse), mosaic and a pixel eraser, plus undo (`Ctrl+Z`), save-as (`Ctrl+S`), finish and cancel.  Selecting a tool expands a sub-toolbar with its options — color and thickness for ink tools, shape kind for the shape tool, block size for mosaic, brush size for the eraser.  The mosaic drag rectangle uses alternating black/white dashes so it stays visible on any content, and the eraser erases exactly where the brush passes (annotations drawn afterwards are unaffected).  Annotations are kept as a chronological operation list in capture-frame pixels and baked into the HDR frame on completion (ink at the SDR white level, mosaic averaged in linear light, eraser as alpha attenuation), so the export keeps full HDR precision.

- **Enter**, **double-click**, or the ✓ button finish the capture and put an SDR rendering on the clipboard (bitmap + PNG formats); no file is written.
- **Save-as** exports the annotated HDR file to a chosen path in the configured output format.

The frozen preview is display-only and SDR-referenced: SDR content matches the live desktop exactly, while HDR highlights clip to white.  Choosing a window or region never discards HDR precision — the exporter always receives the original scRGB frame.

Screenshots are saved automatically in `%USERPROFILE%\Pictures\HDR Capture` (configurable). Right-click the tray icon for:

- **截图** — the interactive capture above.
- **选择窗口/显示器（系统选择器）** — the legacy Windows capture picker, which can also capture occluded/background windows.
- **设置…** — rebind the hotkey, choose the output format (HDR PNG / Ultra HDR JPEG / PNG+gMAP), and set the save directory. Settings persist in `%APPDATA%\HdrCapture\settings.json`.
- **打开保存目录** / **退出**.

If the configured hotkey is already owned by another application, HDR Capture shows a notification; the tray commands still work and you can rebind the hotkey in settings.

The overlay currently captures the single monitor under the cursor. Multi-monitor virtual-desktop capture is not yet implemented.

The source includes a headless container test:

```powershell
dotnet run --project .\HdrCapture.csproj -- --verify-container
```

It synthesizes a floating-point HDR frame and verifies all three output formats: the Ultra HDR JPEG (MPF + gain-map XMP), the PNG gain-map container (`gMAP`), and the single-file HDR PNG (cICP/cLLI/mDCV/iCCP signaling, no companion files).

## Capture assumptions

`R16G16B16A16_FLOAT` captures the compositor's scRGB surface.  The exporter treats the three channels as linear scRGB, creates an SDR base with a shared per-pixel scale, and stores the inverse scale as a logarithmic gain map.  The method is reversible up to SDR/gain-map 8-bit quantization.  On an SDR display or an SDR-only captured application, the output remains valid but has little or no HDR gain.
