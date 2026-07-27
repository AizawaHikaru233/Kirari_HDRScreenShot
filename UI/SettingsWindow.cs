using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Forms = System.Windows.Forms;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfColor = System.Windows.Media.Color;

namespace HdrCapture;

/// <summary>Windows-11-style toggle switch built from plain borders.</summary>
internal sealed class ToggleSwitch : Border
{
    private static readonly Brush OnBrush = new SolidColorBrush(WpfColor.FromRgb(0x00, 0x67, 0xC0));
    private readonly Brush _offBrush;
    private readonly Border _knob;
    private readonly TranslateTransform _knobOffset = new();
    private bool _isOn;

    public bool IsOn
    {
        get => _isOn;
        set { _isOn = value; UpdateVisual(); }
    }

    public ToggleSwitch(bool dark = false)
    {
        _offBrush = new SolidColorBrush(dark ? WpfColor.FromRgb(0x4A, 0x4F, 0x57) : WpfColor.FromRgb(0xC8, 0xCC, 0xD4));
        Width = 44;
        Height = 24;
        CornerRadius = new CornerRadius(12);
        Cursor = Cursors.Hand;
        VerticalAlignment = VerticalAlignment.Center;
        _knob = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = Brushes.White,
            Margin = new Thickness(3),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = _knobOffset,
        };
        Child = _knob;
        MouseLeftButtonDown += (_, e) => { IsOn = !IsOn; e.Handled = true; };
        UpdateVisual();
    }

    private void UpdateVisual()
    {
        Background = _isOn ? OnBrush : _offBrush;
        var targetOffset = _isOn ? 20d : 0d;
        if (!IsLoaded)
        {
            _knobOffset.X = targetOffset;
            return;
        }

        _knobOffset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
        {
            To = targetOffset,
            Duration = TimeSpan.FromMilliseconds(140),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }
}

/// <summary>
/// Modern (borderless, card-based) settings window with light/dark/auto theming: hotkey,
/// output format, save directory, file name pattern, save toggles, auto start, tray icon.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private readonly bool _dark;
    private readonly Brush _textPrimary;
    private readonly Brush _textSecondary;
    private readonly Brush _cardBg;
    private readonly Brush _cardBorder;
    private readonly Brush _fieldBg;
    private static readonly Brush Accent = new SolidColorBrush(WpfColor.FromRgb(0x00, 0x67, 0xC0));

    private HotkeyConfig _hotkey;
    private readonly TextBox _hotkeyBox;
    private readonly TextBox _directoryBox;
    private readonly TextBox _patternBox;
    private readonly ToggleSwitch _saveOnFinish;
    private readonly ToggleSwitch _saveSdrCopy;
    private readonly ToggleSwitch _normalizeSdrWhite;
    private readonly ToggleSwitch _autoStart;
    private readonly ToggleSwitch _hideTray;
    private readonly StackPanel _formatChips = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _themeChips = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _languageChips = new() { Orientation = Orientation.Horizontal };
    private readonly TextBox _referenceSdrWhiteBox;
    private readonly bool _hdrPngAvailable;
    private TextBlock? _updateStatus;
    private string _format;
    private string _theme;
    private string _language;

    public AppSettings Result { get; private set; }
    public bool AutoStartEnabled { get; private set; }

    public SettingsWindow(AppSettings current)
    {
        _hotkey = CloneHotkey(current.CaptureHotkey);
        Result = current;
        _format = current.OutputFormat;
        _theme = current.Theme;
        _language = current.Language;
        _dark = ThemeService.IsDark(ThemeService.Parse(current.Theme));
        var (monitor, _) = NativeMethods.MonitorUnderCursor();
        _hdrPngAvailable = DisplayInfo.ForMonitor(monitor) is not { HdrActive: false };
        if (!_hdrPngAvailable && _format.Equals("hdrpng", StringComparison.OrdinalIgnoreCase))
            _format = "sdrpng";

        _textPrimary = new SolidColorBrush(_dark ? WpfColor.FromRgb(0xF2, 0xF3, 0xF5) : WpfColor.FromRgb(0x1A, 0x1A, 0x1A));
        _textSecondary = new SolidColorBrush(_dark ? WpfColor.FromRgb(0x9A, 0xA0, 0xA8) : WpfColor.FromRgb(0x6B, 0x72, 0x80));
        _cardBg = new SolidColorBrush(_dark ? WpfColor.FromRgb(0x2B, 0x2D, 0x31) : WpfColor.FromRgb(0xFF, 0xFF, 0xFF));
        _cardBorder = new SolidColorBrush(_dark ? WpfColor.FromRgb(0x3C, 0x3F, 0x45) : WpfColor.FromRgb(0xE5, 0xE7, 0xEB));
        _fieldBg = new SolidColorBrush(_dark ? WpfColor.FromRgb(0x23, 0x25, 0x29) : WpfColor.FromRgb(0xFF, 0xFF, 0xFF));

        Title = L.T("Kirari 设置", "Kirari Settings");
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.Height;
        Width = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;

        _hotkeyBox = FieldBox(_hotkey.Describe(), readOnly: true);
        _hotkeyBox.PreviewKeyDown += OnHotkeyKeyDown;

        _directoryBox = FieldBox(current.SaveDirectory ?? string.Empty, readOnly: false);
        _patternBox = FieldBox(current.FileNamePattern, readOnly: false);
        _saveOnFinish = new ToggleSwitch(_dark) { IsOn = current.SaveFileOnFinish };
        _saveSdrCopy = new ToggleSwitch(_dark) { IsOn = current.SaveSdrCopy };
        _normalizeSdrWhite = new ToggleSwitch(_dark) { IsOn = current.NormalizeSdrWhite };
        _referenceSdrWhiteBox = FieldBox(current.ReferenceSdrWhiteNits.ToString("0", System.Globalization.CultureInfo.InvariantCulture), readOnly: false);
        _autoStart = new ToggleSwitch(_dark) { IsOn = AutoStart.IsEnabled() };
        _hideTray = new ToggleSwitch(_dark) { IsOn = current.HideTrayIcon };

        RebuildFormatChips();
        RebuildThemeChips();
        RebuildLanguageChips();

        var browse = FlatButton(L.T("浏览…", "Browse…"), accent: false);
        browse.Margin = new Thickness(8, 0, 0, 0);
        browse.Click += OnBrowse;
        var directoryRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(browse, Dock.Right);
        directoryRow.Children.Add(browse);
        directoryRow.Children.Add(Rounded(_directoryBox));

        var save = FlatButton(L.T("保存", "Save"), accent: true);
        save.Click += OnSave;
        var cancel = FlatButton(L.T("取消", "Cancel"), accent: false);
        cancel.Margin = new Thickness(8, 0, 0, 0);
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);

        var content = new StackPanel { Margin = new Thickness(22) };
        content.Children.Add(BuildTitleBar());
        content.Children.Add(Card(L.T("快捷键", "Hotkey"),
            Label(L.T("触发截图（点击输入框后按下组合键）", "Capture trigger (click the box, then press the combination)")),
            Rounded(_hotkeyBox)));
        content.Children.Add(Card(L.T("保存", "Saving"),
            Label(L.T("输出格式", "Output format")),
            _formatChips,
            Label(L.T("保存目录（留空使用 图片\\HDR Capture）", "Save folder (empty = Pictures\\HDR Capture)")),
            directoryRow,
            Label(L.T("文件名格式（{…} 内为日期格式；SDR 不加后缀，HDR 自动加 _HDR）",
                "File name pattern ({…} = date format; SDR plain, HDR gets _HDR)")),
            Rounded(_patternBox),
            ToggleRow(L.T("完成时同时保存文件", "Also save a file on finish"),
                L.T("按 Enter 复制到剪贴板的同时，也将文件写入保存目录",
                    "Pressing Enter copies to the clipboard and also writes a file to the save folder"), _saveOnFinish),
            ToggleRow(L.T("HDR 截图时额外保存 SDR PNG", "Save an SDR PNG alongside HDR captures"),
                L.T("输出 name_HDR.png 时同时生成 name.png 普通截图",
                    "Writing name_HDR.png also produces a plain name.png"), _saveSdrCopy),
            ToggleRow(L.T("统一 HDR 下 SDR 白点", "Normalize HDR SDR white"),
                L.T("忽略拍摄端系统 SDR 亮度差异；HDR 与 SDR 预览都使用下方参考白点",
                    "Normalizes captures from different Windows SDR-brightness settings to the reference below"), _normalizeSdrWhite),
            Label(L.T("统一 SDR 白点（nit，建议 203）", "Reference SDR white (nit, 203 recommended)")),
            Rounded(_referenceSdrWhiteBox)));
        content.Children.Add(Card(L.T("常规", "General"),
            Label(L.T("界面主题（截图工具栏同步生效）", "Theme (capture toolbar follows)")),
            _themeChips,
            Label(L.T("语言 / Language", "Language / 语言")),
            _languageChips,
            ToggleRow(L.T("开机自启", "Start with Windows"),
                L.T("登录 Windows 后自动在后台运行", "Run silently in the background after signing in"), _autoStart),
            ToggleRow(L.T("隐藏托盘图标", "Hide tray icon"),
                L.T("隐藏后仅快捷键可用；再次运行程序可重新打开设置",
                    "Only the hotkey remains; launch the app again to reopen settings"), _hideTray)));
        content.Children.Add(buttons);

        // Keep the settings window compact: capture/output controls and application details
        // live on separate pages while the bottom Save/Cancel commands remain shared.
        var titleBar = content.Children[0];
        var captureHotkeyCard = content.Children[1];
        var captureSavingCard = content.Children[2];
        var generalCard = content.Children[3];
        content.Children.Clear();

        var capturePage = new StackPanel();
        capturePage.RenderTransform = new TranslateTransform();
        capturePage.Children.Add(captureHotkeyCard);
        capturePage.Children.Add(captureSavingCard);
        var generalPage = new StackPanel { Visibility = Visibility.Collapsed };
        generalPage.RenderTransform = new TranslateTransform();
        generalPage.Children.Add(generalCard);
        generalPage.Children.Add(BuildAboutCard());

        var captureTab = FlatButton(L.T("截图与保存", "Capture & Save"), accent: true);
        var generalTab = FlatButton(L.T("通用与关于", "General & About"), accent: false);
        generalTab.Margin = new Thickness(8, 0, 0, 0);
        var tabs = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 14),
        };
        tabs.Children.Add(captureTab);
        tabs.Children.Add(generalTab);

        var pages = new Grid();
        pages.Children.Add(capturePage);
        pages.Children.Add(generalPage);
        void ShowPage(bool capture)
        {
            var visiblePage = capture ? capturePage : generalPage;
            var hiddenPage = capture ? generalPage : capturePage;
            if (visiblePage.Visibility != Visibility.Visible)
            {
                hiddenPage.Visibility = Visibility.Collapsed;
                visiblePage.Visibility = Visibility.Visible;
                visiblePage.Opacity = 0;
                var translation = (TranslateTransform)visiblePage.RenderTransform;
                translation.X = capture ? -12 : 12;
                visiblePage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
                translation.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(translation.X, 0, TimeSpan.FromMilliseconds(160))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
            }
            captureTab.Background = capture ? Accent : _fieldBg;
            captureTab.Foreground = capture ? Brushes.White : _textPrimary;
            captureTab.BorderBrush = capture ? Accent : _cardBorder;
            generalTab.Background = capture ? _fieldBg : Accent;
            generalTab.Foreground = capture ? _textPrimary : Brushes.White;
            generalTab.BorderBrush = capture ? _cardBorder : Accent;
        }
        captureTab.Click += (_, _) => ShowPage(true);
        generalTab.Click += (_, _) => ShowPage(false);

        content.Children.Add(titleBar);
        content.Children.Add(tabs);
        content.Children.Add(pages);
        content.Children.Add(buttons);

        var root = new Border
        {
            Background = new SolidColorBrush(_dark ? WpfColor.FromRgb(0x1F, 0x21, 0x24) : WpfColor.FromRgb(0xF6, 0xF8, 0xFA)),
            CornerRadius = new CornerRadius(12),
            BorderBrush = _cardBorder,
            BorderThickness = new Thickness(1),
            Child = content,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.25 },
            Margin = new Thickness(12),
        };
        root.RenderTransformOrigin = new Point(0.5, 0.5);
        root.RenderTransform = new ScaleTransform(0.98, 0.98);
        root.Opacity = 0;
        Content = root;
        Loaded += (_, _) =>
        {
            root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
            var scale = (ScaleTransform)root.RenderTransform;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.98, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.98, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        };
    }

    // ---------------------------------------------------------------- chips

    private void RebuildFormatChips()
    {
        _formatChips.Children.Clear();
        AddChip(_formatChips, "HDR PNG", "hdrpng", _format, value => { _format = value; RebuildFormatChips(); }, _hdrPngAvailable);
        AddChip(_formatChips, "SDR PNG", "sdrpng", _format, value => { _format = value; RebuildFormatChips(); });
        AddChip(_formatChips, "SDR JPG", "sdrjpg", _format, value => { _format = value; RebuildFormatChips(); });
        var hdrPng = _format.Equals("hdrpng", StringComparison.OrdinalIgnoreCase);
        _saveSdrCopy.IsEnabled = hdrPng;
        _saveSdrCopy.Opacity = hdrPng ? 1 : 0.45;
        _saveSdrCopy.Cursor = hdrPng ? Cursors.Hand : Cursors.Arrow;
        if (!hdrPng) _saveSdrCopy.IsOn = false;
    }

    private void RebuildThemeChips()
    {
        _themeChips.Children.Clear();
        AddChip(_themeChips, L.T("自适应", "Auto"), "auto", _theme, value => { _theme = value; RebuildThemeChips(); });
        AddChip(_themeChips, L.T("浅色", "Light"), "light", _theme, value => { _theme = value; RebuildThemeChips(); });
        AddChip(_themeChips, L.T("深色", "Dark"), "dark", _theme, value => { _theme = value; RebuildThemeChips(); });
    }

    private void RebuildLanguageChips()
    {
        _languageChips.Children.Clear();
        AddChip(_languageChips, L.T("自动", "Auto"), "auto", _language, value => { _language = value; RebuildLanguageChips(); });
        AddChip(_languageChips, "中文", "zh", _language, value => { _language = value; RebuildLanguageChips(); });
        AddChip(_languageChips, "English", "en", _language, value => { _language = value; RebuildLanguageChips(); });
    }

    private void AddChip(StackPanel host, string text, string value, string current, Action<string> pick, bool enabled = true)
    {
        var active = current == value;
        var chip = new Border
        {
            Background = active ? Accent : _fieldBg,
            BorderBrush = active ? Accent : _cardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = enabled ? Cursors.Hand : Cursors.Arrow,
            Opacity = enabled ? 1 : 0.45,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12.5,
                Foreground = active ? Brushes.White : _textPrimary,
            },
        };
        if (enabled)
            chip.MouseLeftButtonDown += (_, e) => { pick(value); e.Handled = true; };
        host.Children.Add(chip);
    }

    // ---------------------------------------------------------------- building blocks

    private UIElement BuildTitleBar()
    {
        var title = new TextBlock
        {
            Text = L.T("Kirari 设置", "Kirari Settings"),
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var close = new Button
        {
            Content = new TextBlock { Text = "\uE711", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13 },
            Width = 32,
            Height = 32,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Foreground = _textSecondary,
            Focusable = false,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        close.Click += (_, _) => Close();
        var bar = new DockPanel { Margin = new Thickness(2, 0, 0, 14), LastChildFill = true };
        DockPanel.SetDock(close, Dock.Right);
        bar.Children.Add(close);
        bar.Children.Add(title);
        bar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        bar.Background = Brushes.Transparent;
        return bar;
    }

    private Border Card(string header, params UIElement[] children)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = header,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textSecondary,
            Margin = new Thickness(0, 0, 0, 6),
        });
        foreach (var child in children)
            panel.Children.Add(child);
        return new Border
        {
            Background = _cardBg,
            CornerRadius = new CornerRadius(8),
            BorderBrush = _cardBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 12, 16, 14),
            Margin = new Thickness(0, 0, 0, 12),
            Child = panel,
        };
    }

    private TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = _textSecondary,
        Margin = new Thickness(0, 10, 0, 4),
    };

    private TextBox FieldBox(string text, bool readOnly) => new()
    {
        Text = text,
        IsReadOnly = readOnly,
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        Foreground = _textPrimary,
        CaretBrush = _textPrimary,
        Padding = new Thickness(2, 0, 2, 0),
        FontSize = 13,
        VerticalContentAlignment = VerticalAlignment.Center,
        Cursor = readOnly ? Cursors.Hand : Cursors.IBeam,
    };

    private Border Rounded(TextBox inner) => new()
    {
        Background = _fieldBg,
        CornerRadius = new CornerRadius(6),
        BorderBrush = _cardBorder,
        BorderThickness = new Thickness(1),
        Padding = new Thickness(8, 7, 8, 7),
        Child = inner,
    };

    private UIElement ToggleRow(string title, string subtitle, ToggleSwitch toggle)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 13, Foreground = _textPrimary });
        text.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 11,
            Foreground = _textSecondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 12, 0),
        });
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 2), LastChildFill = true };
        DockPanel.SetDock(toggle, Dock.Right);
        row.Children.Add(toggle);
        row.Children.Add(text);
        return row;
    }

    private UIElement BuildAboutCard()
    {
        var github = FlatButton("GitHub", accent: false);
        github.Click += (_, _) => ProjectInfo.OpenGitHub();
        var checkUpdate = FlatButton(L.T("检查更新", "Check for updates"), accent: true);
        checkUpdate.Margin = new Thickness(8, 0, 0, 0);
        checkUpdate.Click += async (_, _) => await CheckForUpdatesAsync(checkUpdate);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        actions.Children.Add(github);
        actions.Children.Add(checkUpdate);

        _updateStatus = new TextBlock
        {
            Foreground = _textSecondary,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        };
        return Card(L.T("关于", "About"),
            Label($"Kirari v{ProjectInfo.CurrentVersionText}"),
            Label(ProjectInfo.GitHubUrl.AbsoluteUri),
            actions,
            _updateStatus);
    }

    private async Task CheckForUpdatesAsync(Button checkUpdate)
    {
        if (_updateStatus is null) return;
        checkUpdate.IsEnabled = false;
        _updateStatus.Foreground = _textSecondary;
        _updateStatus.Text = L.T("正在检查 GitHub 更新...", "Checking GitHub for updates...");
        try
        {
            var result = await ProjectInfo.CheckForUpdatesAsync();
            if (result.IsUpdateAvailable)
            {
                _updateStatus.Foreground = Accent;
                _updateStatus.Text = L.T($"发现新版本 {result.LatestVersion}，已打开发布页。", $"Version {result.LatestVersion} is available. Opening the release page.");
                ProjectInfo.OpenUrl(result.ReleaseUrl);
            }
            else
            {
                _updateStatus.Text = L.T("当前已是最新版本。", "You are up to date.");
            }
        }
        catch (Exception ex)
        {
            _updateStatus.Text = L.T($"检查更新失败：{ex.Message}", $"Update check failed: {ex.Message}");
        }
        finally
        {
            checkUpdate.IsEnabled = true;
        }
    }

    private Button FlatButton(string text, bool accent)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(22, 7, 22, 7),
            FontSize = 13,
            Focusable = false,
            Background = accent ? Accent : _fieldBg,
            Foreground = accent ? Brushes.White : _textPrimary,
            BorderBrush = accent ? Accent : _cardBorder,
        };
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        button.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        return button;
    }

    // ---------------------------------------------------------------- behavior

    private void OnHotkeyKeyDown(object sender, WpfKeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (IsModifierKey(key)) return;

        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None) return; // require a modifier to avoid stealing plain keys

        _hotkey = new HotkeyConfig
        {
            Control = modifiers.HasFlag(ModifierKeys.Control),
            Shift = modifiers.HasFlag(ModifierKeys.Shift),
            Alt = modifiers.HasFlag(ModifierKeys.Alt),
            Win = modifiers.HasFlag(ModifierKeys.Windows),
            VirtualKey = (uint)KeyInterop.VirtualKeyFromKey(key),
            KeyName = DescribeKey(key),
        };
        _hotkeyBox.Text = _hotkey.Describe();
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog();
        if (!string.IsNullOrWhiteSpace(_directoryBox.Text)) dialog.SelectedPath = _directoryBox.Text;
        if (dialog.ShowDialog() == Forms.DialogResult.OK) _directoryBox.Text = dialog.SelectedPath;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!_hotkey.IsValid)
        {
            MessageBox.Show(this,
                L.T("快捷键至少需要一个修饰键（Ctrl/Alt/Shift/Win）加一个按键。",
                    "The hotkey needs at least one modifier (Ctrl/Alt/Shift/Win) plus a key."),
                "Kirari", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = new AppSettings
        {
            CaptureHotkey = _hotkey,
            OutputFormat = _format,
            SaveDirectory = string.IsNullOrWhiteSpace(_directoryBox.Text) ? null : _directoryBox.Text.Trim(),
            FileNamePattern = string.IsNullOrWhiteSpace(_patternBox.Text) ? "Kirari_{yyyyMMdd_HHmmss}" : _patternBox.Text.Trim(),
            SaveFileOnFinish = _saveOnFinish.IsOn,
            SaveSdrCopy = _format.Equals("hdrpng", StringComparison.OrdinalIgnoreCase) && _saveSdrCopy.IsOn,
            NormalizeSdrWhite = _normalizeSdrWhite.IsOn,
            ReferenceSdrWhiteNits = ParseReferenceSdrWhite(_referenceSdrWhiteBox.Text),
            HideTrayIcon = _hideTray.IsOn,
            Theme = _theme,
            Language = _language,
        };
        AutoStartEnabled = _autoStart.IsOn;
        DialogResult = true;
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System or Key.None;

    private static string DescribeKey(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (key - Key.NumPad0),
        _ => key.ToString(),
    };

    private static float ParseReferenceSdrWhite(string text) =>
        float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) &&
        float.IsFinite(value) && value >= 40f && value <= 500f ? value : 203f;

    private static HotkeyConfig CloneHotkey(HotkeyConfig source) => new()
    {
        Control = source.Control,
        Shift = source.Shift,
        Alt = source.Alt,
        Win = source.Win,
        VirtualKey = source.VirtualKey,
        KeyName = source.KeyName,
    };
}
