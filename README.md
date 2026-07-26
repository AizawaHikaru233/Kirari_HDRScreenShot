# Kirari（キラリ）

<p><b>中文</b> | <a href="#english">English</a></p>

基于 Windows Graphics Capture 的 HDR 截图工具。以 `R16G16B16A16_FLOAT`（线性 scRGB）精度捕获显示器或窗口，保存为单文件 HDR PNG；同一个文件在不支持 HDR 的查看器里也能正确显示 SDR 效果。可执行文件为 `Kirari.exe`。

## 功能

- **快速上手**：全局快捷键（默认 `Ctrl+Shift+A`）或双击托盘图标，自动识别光标下的窗口，也可拖拽框选任意区域；选区可再拖动移动、拉动边缘缩放。
- **真 HDR 取景预览**：框选时画面通过 FP16 交换链原样显示 HDR 亮度，选区外压暗；SDR 内容与桌面完全一致。
- **标注工具栏**：涂鸦、箭头、框选（矩形 / 椭圆）、马赛克、像素级橡皮擦，均带颜色 / 粗细等子选项；`Ctrl+Z` 撤销。标注按时间顺序在完成时以线性光合成进 HDR 帧，不损失 HDR 精度。
- **放大镜取色**：像素网格、坐标与光标颜色实时显示，`C` 复制颜色值，`Shift` 切换 RGB / 十六进制。
- **长截图**：自动滚动目标窗口并拼接成一张长图。
- **文字识别（OCR）**：基于 RapidOCR（PP-OCRv5，中英），识别结果直接复制到剪贴板。
- **HDR 剪贴板**：完成（Enter / 双击）时同时写入 SDR 位图和 HDR PNG 两种剪贴板格式；支持 HDR 粘贴的应用（Chromium 系）可直接拿到 HDR 图。
- **双语界面**：中文 / English，可在设置中切换（默认跟随系统）。

## 输出格式

| 格式 | 文件 | 说明 |
| --- | --- | --- |
| HDR PNG（默认） | 单个 `name_HDR.png` | 16 位 Rec.2020 PQ 像素，带 `cICP`、`cLLI`、`mDCV`、`iCCP`、`cHRM`、`sBIT` 信号（ReShade / Special K 风格）。HDR 查看器按 `cICP` 显示 HDR；色彩管理的 SDR 查看器按内嵌 ICC 显示 SDR 裁切效果 |
| SDR PNG | 单个 `.png` | 普通 8 位 sRGB 截图，按捕获时的预览效果（SDR 白基准、HDR 高光裁切）生成，不含任何 HDR 数据 |
| SDR JPG | 单个 `.jpg` | 同上，JPEG 编码（质量 92），不含任何 HDR 数据 |

选择 HDR PNG 时可在设置中开启“额外保存 SDR PNG”：输出 `name_HDR.png` 的同时生成一张普通 `name.png`，方便直接分享。

### HDR PNG 细节

- `cLLI` 的 MaxCLL 取帧亮度的 99.99 百分位而非绝对峰值——Chromium 按 MaxCLL 缩放 SDR 色调映射，孤立的高光像素会让整张图变暗（与 ledoge / Special K 的做法一致）。
- `mDCV` 携带捕获显示器通过 DXGI（`IDXGIOutput6`）查询到的真实色域与亮度范围；不可用时（如系统选择器路径）回退为 Rec.2020 色域加内容峰值。
- `iCCP` 内嵌一个生成的 ICC v4 配置文件，其色调曲线解码 PQ 并在捕获显示器的 SDR 白电平处裁切，让忽略 `cICP` 的色彩管理查看器复现取景时的 SDR 效果。按 PNG 规范 `cICP` 优先于 `iCCP`，HDR 查看器不受影响。
- 绝不写入 `sRGB` / `gAMA` 块（会破坏 Discord / SKIV 的 HDR 检测）。兼容性（对照 ReShade / Special K 源码，2026-07）：Chromium 系（Chrome、Edge、Discord/Electron）识别 `cICP`，Discord 显示 HDR 需要 `iCCP` 存在；Windows 照片 / 资源管理器的 `cICP` 支持未验证，预期走 `iCCP` 回退。
- 旧文件可用 `Kirari.exe --resign <file.png>` 重新签名（输出 `<file>_fixed.png`）。

## 构建与运行

需要 Windows 10 SDK 10.0.26100 与 .NET 10 SDK。

```powershell
dotnet build .\HdrCapture.csproj
dotnet run --project .\HdrCapture.csproj
```

程序静默启动到通知区域，不显示主窗口。截图默认保存在 `%USERPROFILE%\Pictures\HDR Capture`（可配置），设置保存在 `%APPDATA%\Kirari\settings.json`。

托盘右键菜单：**截图**（交互式截图）、**选择窗口/显示器**（Windows 系统选择器，可捕获被遮挡 / 后台窗口）、**设置…**（快捷键、输出格式、保存目录、文件名格式、主题、语言等）、**打开保存目录**、**退出**。若快捷键被其他程序占用会弹出通知，可在设置中改绑。

自带无头容器自检：

```powershell
dotnet run --project .\HdrCapture.csproj -- --verify-container
```

合成一张浮点 HDR 帧并验证三种输出：HDR PNG 的完整信号（cICP/cLLI/mDCV/iCCP，且无伴随文件）、SDR PNG / SDR JPG 的文件签名，以及“额外保存 SDR PNG”的配对命名。

## 已知限制

- 截图覆盖层只捕获光标所在的单个显示器，暂不支持跨显示器虚拟桌面截图。
- 在开启垂直同步的全屏游戏上，覆盖层帧率可能受平台合成限制（约 60fps），不影响截图质量。

## 许可

本项目以 **GPL-3.0** 发布（见 [LICENSE](LICENSE)）。仓库内所有代码均为原创；全部依赖均为开源（MIT / Apache-2.0，见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)），与 GPL-3.0 单向兼容。PNG 编码器、PNG 块容器与 ICC 配置文件生成器均为自研实现；JPEG 编码使用系统自带的 WIC 编解码器（系统库例外）。不包含任何闭源组件。

---

<a id="english"></a>

# Kirari (English)

A Windows Graphics Capture HDR screenshot utility. It captures a display or window at `R16G16B16A16_FLOAT` (linear scRGB) precision and saves a single-file HDR PNG that also renders correctly as SDR in non-HDR viewers. The executable is `Kirari.exe`.

## Features

- **Quick start**: global hotkey (default `Ctrl+Shift+A`) or tray double-click; auto-detects the window under the cursor, or drag to select any region; the selection can then be moved and resized.
- **True HDR live preview**: the frozen frame is presented through an FP16 swap chain, so HDR brightness shows as-is while the area outside the selection is dimmed; SDR content matches the desktop exactly.
- **Annotation toolbar**: pen, arrow, shape (rectangle/ellipse), mosaic and a pixel-level eraser, each with color/thickness sub-options; `Ctrl+Z` to undo. Annotations are composited into the HDR frame in linear light on completion, preserving full HDR precision.
- **Magnifier loupe**: pixel grid, coordinates and the color under the cursor; `C` copies the color value, `Shift` toggles RGB/hex.
- **Long screenshot**: auto-scrolls the target window and stitches the frames into one tall image.
- **Text recognition (OCR)**: powered by RapidOCR (PP-OCRv5, Chinese + English); the recognized text goes straight to the clipboard.
- **HDR clipboard**: finishing (Enter / double-click) puts both an SDR bitmap and an HDR PNG on the clipboard; HDR-aware paste targets (Chromium family) receive the HDR image.
- **Bilingual UI**: Chinese / English, switchable in Settings (follows the system by default).

## Output formats

| Format | Files | Notes |
| --- | --- | --- |
| HDR PNG (default) | single `name_HDR.png` | 16-bit Rec.2020 PQ pixels with `cICP`, `cLLI`, `mDCV`, `iCCP`, `cHRM`, `sBIT` signaling (ReShade / Special K style). HDR-aware viewers follow `cICP`; color-managed SDR viewers follow the embedded ICC profile and show the SDR-clipped look |
| SDR PNG | single `.png` | plain 8-bit sRGB screenshot rendered like the capture preview (SDR-white referenced, HDR highlights clipped); contains no HDR data |
| SDR JPG | single `.jpg` | same rendering, JPEG-encoded (quality 92); contains no HDR data |

With HDR PNG selected, an optional setting also saves an SDR PNG: writing `name_HDR.png` additionally produces a plain `name.png` for easy sharing.

### HDR PNG details

- `cLLI` MaxCLL is the 99.99th-percentile frame peak rather than the absolute maximum — Chromium scales its SDR tone map by MaxCLL, and a lone specular pixel would otherwise dim the whole image (the same approach ledoge / Special K use).
- `mDCV` carries the capture monitor's real color primaries and luminance range queried through DXGI (`IDXGIOutput6`); when unavailable (e.g. captures made through the system picker), it falls back to Rec.2020 primaries with a content-derived peak.
- `iCCP` embeds a generated ICC v4 profile whose tone curve decodes PQ and clips at the capture monitor's SDR white level, so color-managed SDR viewers that ignore `cICP` reproduce the overlay's SDR-referenced look. Per the PNG spec `cICP` outranks `iCCP`, so HDR-aware viewers are unaffected.
- `sRGB`/`gAMA` chunks are never written (they break Discord/SKIV HDR detection). Compatibility (verified against ReShade/Special K sources, 2026-07): Chromium-family viewers (Chrome, Edge, Discord/Electron) honor `cICP`, and Discord HDR requires `iCCP` to be present; Windows Photos/Explorer `cICP` support is unverified — expect the `iCCP` fallback there.
- A previously saved capture can be re-signed in place with `Kirari.exe --resign <file.png>` (writes `<file>_fixed.png`).

## Build and run

Requires the Windows 10 SDK 10.0.26100 and the .NET 10 SDK.

```powershell
dotnet build .\HdrCapture.csproj
dotnet run --project .\HdrCapture.csproj
```

The application starts silently in the notification area; it does not open a main window. Screenshots are saved to `%USERPROFILE%\Pictures\HDR Capture` (configurable); settings persist in `%APPDATA%\Kirari\settings.json`.

Tray menu: **Capture** (the interactive capture), **Pick window/display** (the Windows system picker, which can also capture occluded/background windows), **Settings…** (hotkey, output format, save folder, file name pattern, theme, language), **Open save folder**, **Exit**. If the hotkey is owned by another application a notification appears and it can be rebound in Settings.

A headless container self-test is included:

```powershell
dotnet run --project .\HdrCapture.csproj -- --verify-container
```

It synthesizes a floating-point HDR frame and verifies all three outputs: the HDR PNG signaling (cICP/cLLI/mDCV/iCCP, no companion files), the SDR PNG / SDR JPG file signatures, and the save-SDR-copy pairing.

## Known limitations

- The capture overlay covers the single monitor under the cursor; multi-monitor virtual-desktop capture is not implemented.
- Over full-screen vsync'd games the overlay frame rate can be limited by platform composition (~60 fps); capture quality is unaffected.

## Licensing

Released under **GPL-3.0** (see [LICENSE](LICENSE)). All code in this repository is original. Every dependency is open source (MIT / Apache-2.0 — see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)), all one-way compatible with GPL-3.0. The PNG encoder, PNG chunk containers and the ICC profile generator are in-house implementations; JPEG encoding uses the OS-provided WIC codec (system-library exception). No closed-source components are bundled.
