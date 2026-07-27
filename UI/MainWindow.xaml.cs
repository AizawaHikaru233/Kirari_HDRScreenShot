using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;
using Windows.Graphics.Capture;

namespace HdrCapture;

public partial class MainWindow : Window, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int CaptureHotkeyId = 1;

    private HwndSource? _source;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ToolStripMenuItem? _captureMenuItem;
    private AppSettings _settings = new();
    private int _captureInProgress;
    private bool _hotkeyRegistered;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        Closing += (_, e) => e.Cancel = !_allowClose;
    }

    public void StartBackground()
    {
        _settings = AppSettings.Load();
        L.Apply(_settings.Language);
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
        CreateTrayIcon();
        RegisterConfiguredHotkey();
        // Refresh the autostart path (covers the executable rename) if the feature is on.
        try { if (AutoStart.IsEnabled()) AutoStart.SetEnabled(true); } catch { }
        // Settle to a lean baseline once startup/JIT churn is over.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, MemoryTrimmer.Trim);
    }

    private Forms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        _captureMenuItem = new Forms.ToolStripMenuItem(L.T("截图", "Capture"), null, (_, _) => _ = CaptureInteractiveAsync());
        menu.Items.Add(_captureMenuItem);
        menu.Items.Add(L.T("选择窗口/显示器（系统选择器）", "Pick window/display (system picker)"), null, (_, _) => _ = CaptureWithPickerAsync());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(L.T("设置…", "Settings…"), null, (_, _) => OpenSettings());
        menu.Items.Add(L.T("打开保存目录", "Open save folder"), null, (_, _) => OpenOutputDirectory());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(L.T("退出", "Exit"), null, (_, _) => RequestExit());
        return menu;
    }

    private void CreateTrayIcon()
    {
        var menu = BuildTrayMenu();
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = (Environment.ProcessPath is { } exePath ? System.Drawing.Icon.ExtractAssociatedIcon(exePath) : null)
                ?? System.Drawing.SystemIcons.Application,
            Visible = !_settings.HideTrayIcon,
            ContextMenuStrip = menu,
        };
        _trayIcon.DoubleClick += (_, _) => _ = CaptureInteractiveAsync();
        UpdateTrayText();
    }

    private void Notify(string title, string message, Forms.ToolTipIcon icon = Forms.ToolTipIcon.Info)
    {
        if (_trayIcon is { Visible: true } tray)
            tray.ShowBalloonTip(3500, title, message, icon);
    }

    private void RegisterConfiguredHotkey()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        UnregisterHotKey(hwnd, CaptureHotkeyId);

        var hotkey = _settings.CaptureHotkey;
        _hotkeyRegistered = hotkey.IsValid &&
            RegisterHotKey(hwnd, CaptureHotkeyId, hotkey.Modifiers | HotkeyConfig.ModNoRepeat, hotkey.VirtualKey);

        if (!_hotkeyRegistered)
            Notify(L.T("Kirari 快捷键不可用", "Kirari hotkey unavailable"),
                L.T($"{hotkey.Describe()} 可能已被占用，请在设置中更换，或使用托盘菜单截图。",
                    $"{hotkey.Describe()} may be taken by another app. Change it in Settings, or capture from the tray menu."),
                Forms.ToolTipIcon.Warning);

        UpdateTrayText();
    }

    private void UpdateTrayText()
    {
        var hotkeyText = _settings.CaptureHotkey.Describe();
        if (_captureMenuItem is not null)
            _captureMenuItem.Text = $"{L.T("截图", "Capture")}  ({hotkeyText})";
        if (_trayIcon is not null)
            _trayIcon.Text = L.T($"Kirari\n{hotkeyText}: 截图（自动识别窗口 / 框选）",
                $"Kirari\n{hotkeyText}: capture (window detect / region)");
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam == CaptureHotkeyId)
        {
            _ = CaptureInteractiveAsync();
            handled = true;
        }
        return 0;
    }

    /// <summary>PixPin-style capture: freeze the monitor under the cursor, then pick a window or region.</summary>
    private async Task CaptureInteractiveAsync()
    {
        if (Interlocked.Exchange(ref _captureInProgress, 1) != 0) return;
        try
        {
            if (!GraphicsCaptureSession.IsSupported())
                throw new InvalidOperationException("Windows Graphics Capture is unavailable on this system.");

            var (monitor, bounds) = NativeMethods.MonitorUnderCursor();
            var item = MonitorCapture.CreateForMonitor(monitor);
            // Display metadata is independent of the frame; query it concurrently.
            var displayTask = Task.Run(() => DisplayInfo.ForMonitor(monitor));
            var frame = await Task.Run(() =>
                GraphicsCaptureService.CaptureOneFrame(item, captureCursor: false, hideBorder: true));
            frame = frame with { Display = await displayTask };
            var outputFormat = GetOutputFormatForHdrAvailability(frame.Display?.HdrActive != false);
            var sourceSdrWhiteNits = frame.Display?.SdrWhiteNits ?? DisplayInfo.GetSdrWhiteNits(monitor);

            var windows = WindowEnumerator.Enumerate(bounds, frame.Width, frame.Height, new WindowInteropHelper(this).Handle);
            // Keep the selection backdrop identical to the frozen desktop. The optional
            // SDR-white normalization is applied only after the user has finished editing.
            var sdrWhiteScale = sourceSdrWhiteNits / HdrPngExporter.SdrWhiteNits;
            var chrome = ChromeTheme.Resolve(ThemeService.IsDark(ThemeService.Parse(_settings.Theme)));
            var result = CaptureOverlayWindow.Capture(frame, windows, bounds, sdrWhiteScale, _settings.ResolveOutputDirectory(), _settings.BuildFileName(outputFormat), chrome);
            if (result is null) return;

            var outputFrame = result.Frame;
            if (_settings.NormalizeSdrWhite)
                outputFrame = SdrWhiteNormalizer.NormalizeFrame(outputFrame, sourceSdrWhiteNits, _settings.ReferenceSdrWhiteNits,
                    frame.Display?.MaxNits ?? _settings.ReferenceSdrWhiteNits);
            var outputSdrWhiteScale = (outputFrame.Display?.SdrWhiteNits ?? sourceSdrWhiteNits) / HdrPngExporter.SdrWhiteNits;

            var savePath = result.SavePath;
            if (savePath is null && result.CopyToClipboard && _settings.SaveFileOnFinish)
                savePath = Path.Combine(_settings.ResolveOutputDirectory(), _settings.BuildFileName(outputFormat));

            if (savePath is not null)
                await ExportAsync(outputFrame, savePath, outputFormat, notify: !result.CopyToClipboard);
            if (result.CopyToClipboard)
            {
                var (clipboardBytes, isPng) = await Task.Run(() => outputFormat switch
                {
                    "hdrpng" => (HdrPngExporter.Encode(outputFrame, fast: true).Data, true),
                    "sdrpng" => (SdrExporter.EncodePng(outputFrame, outputSdrWhiteScale), true),
                    "sdrjpg" => (SdrExporter.EncodeJpg(outputFrame, outputSdrWhiteScale), false),
                    _ => throw new InvalidOperationException($"Unsupported output format: {outputFormat}"),
                });
                ClipboardWriter.Copy(outputFrame, outputSdrWhiteScale, clipboardBytes, isPng);
                var clipboardDescription = outputFormat switch
                {
                    "hdrpng" => L.T("SDR 位图 + HDR PNG", "SDR bitmap + HDR PNG"),
                    "sdrpng" => L.T("SDR 位图 + SDR PNG", "SDR bitmap + SDR PNG"),
                    _ => L.T("SDR 位图 + SDR JPG", "SDR bitmap + SDR JPG"),
                };
                Notify("Kirari", savePath is null
                    ? L.T($"已复制到剪贴板（{clipboardDescription}）", $"Copied to clipboard ({clipboardDescription})")
                    : L.T($"已保存 {Path.GetFileName(savePath)} 并复制到剪贴板（{clipboardDescription}）",
                        $"Saved {Path.GetFileName(savePath)} and copied to clipboard ({clipboardDescription})"));
            }
        }
        catch (Exception ex)
        {
            Notify(L.T("Kirari 失败", "Kirari failed"), ex.Message, Forms.ToolTipIcon.Error);
        }
        finally
        {
            Interlocked.Exchange(ref _captureInProgress, 0);
            // A capture session churns hundreds of MB of LOH buffers; hand them back to the OS.
            MemoryTrimmer.Trim();
        }
    }

    /// <summary>Legacy path: the Windows system picker, which can capture occluded/background windows.</summary>
    private async Task CaptureWithPickerAsync()
    {
        if (Interlocked.Exchange(ref _captureInProgress, 1) != 0) return;
        try
        {
            if (!GraphicsCaptureSession.IsSupported())
                throw new InvalidOperationException("Windows Graphics Capture is unavailable on this system.");

            var item = await CapturePickerHelper.PickAsync(new WindowInteropHelper(this).Handle);
            if (item is null) return;
            // GraphicsCaptureItem does not expose its source monitor, so Display stays null here
            // and the exporter falls back to content-derived mDCV metadata for this path.
            var frame = await Task.Run(() => GraphicsCaptureService.CaptureOneFrame(item, captureCursor: true));
            var (monitor, _) = NativeMethods.MonitorUnderCursor();
            var outputFormat = GetOutputFormatForHdrAvailability(DisplayInfo.ForMonitor(monitor)?.HdrActive != false);
            await SaveFrameAsync(frame, outputFormat);
        }
        catch (Exception ex)
        {
            Notify(L.T("Kirari 失败", "Kirari failed"), ex.Message, Forms.ToolTipIcon.Error);
        }
        finally
        {
            Interlocked.Exchange(ref _captureInProgress, 0);
            MemoryTrimmer.Trim();
        }
    }

    private async Task SaveFrameAsync(HdrFrame frame, string outputFormat)
    {
        await ExportAsync(frame, Path.Combine(_settings.ResolveOutputDirectory(), _settings.BuildFileName(outputFormat)), outputFormat);
    }

    private async Task ExportAsync(HdrFrame frame, string outputPath, string outputFormat, bool notify = true)
    {
        var result = await Task.Run(() => CaptureExporter.Export(frame, outputPath, outputFormat, _settings.SaveSdrCopy));
        if (notify)
            Notify("Kirari", L.T($"已保存 {Path.GetFileName(result.MainPath)}", $"Saved {Path.GetFileName(result.MainPath)}"));
    }

    private string GetOutputFormatForHdrAvailability(bool hdrAvailable)
    {
        if (!_settings.OutputFormat.Equals("hdrpng", StringComparison.OrdinalIgnoreCase) || hdrAvailable)
            return _settings.OutputFormat;

        Notify("Kirari", L.T("当前显示器未开启 HDR，已改用 SDR PNG", "HDR is disabled on this display; using SDR PNG instead."), Forms.ToolTipIcon.Warning);
        return "sdrpng";
    }

    private void OpenSettings()
    {
        var dialog = new SettingsWindow(_settings);
        if (dialog.ShowDialog() != true) return;
        _settings = dialog.Result;
        try
        {
            _settings.Save();
            AutoStart.SetEnabled(dialog.AutoStartEnabled);
        }
        catch (Exception ex)
        {
            Notify(L.T("Kirari 设置未保存", "Kirari settings not saved"), ex.Message, Forms.ToolTipIcon.Warning);
        }
        L.Apply(_settings.Language);
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = !_settings.HideTrayIcon;
            var oldMenu = _trayIcon.ContextMenuStrip;
            _trayIcon.ContextMenuStrip = BuildTrayMenu();
            oldMenu?.Dispose();
        }
        RegisterConfiguredHotkey();
    }

    /// <summary>Invoked when a second instance is launched (the escape hatch for a hidden tray icon).</summary>
    internal void ShowSettings()
    {
        if (Application.Current.Windows.OfType<SettingsWindow>().Any()) return;
        OpenSettings();
    }

    private void OpenOutputDirectory()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = _settings.ResolveOutputDirectory(),
            UseShellExecute = true,
        });
    }

    private void RequestExit()
    {
        _allowClose = true;
        if (_trayIcon is not null) _trayIcon.Visible = false;
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0)
            UnregisterHotKey(hwnd, CaptureHotkeyId);
        _source?.RemoveHook(WndProc);
        _trayIcon?.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint virtualKeyCode);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
