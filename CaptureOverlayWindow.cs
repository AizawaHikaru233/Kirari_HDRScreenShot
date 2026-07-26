using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WpfImage = System.Windows.Controls.Image;
using WpfColor = System.Windows.Media.Color;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfPoint = System.Windows.Point;
using WpfPath = System.Windows.Shapes.Path;
using WpfRectangle = System.Windows.Shapes.Rectangle;
using WpfEllipse = System.Windows.Shapes.Ellipse;
using WpfPolyline = System.Windows.Shapes.Polyline;
using WpfShape = System.Windows.Shapes.Shape;

namespace HdrCapture;

/// <summary>The overlay's outcome: the HDR result plus where it goes (file path and/or clipboard).</summary>
internal sealed record OverlayCaptureResult(HdrFrame Frame, string? SavePath, bool CopyToClipboard);

/// <summary>
/// PixPin-style capture overlay. Phase 1 (selecting): hover auto-highlights the window under
/// the cursor; click captures it, drag selects a region. Phase 2 (editing): a toolbar offers
/// pen, arrow, shape (rect/ellipse), mosaic and a pixel eraser; each tool expands a sub-toolbar
/// with its options (color, thickness, shape kind, block/eraser size). Annotations accumulate
/// chronologically in a frame-resolution raster for display and are re-baked into the HDR frame
/// on completion, so the export keeps full precision.
/// </summary>
internal sealed class CaptureOverlayWindow : Window
{
    private const double DragThreshold = 5;

    private enum OverlayMode { Selecting, Editing, Scrolling }
    private enum Tool { None, Pen, Arrow, Shape, Mosaic, Eraser }
    private enum ShapeKind { Rect, Ellipse }

    [Flags]
    private enum ResizeAnchor { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

    private sealed record InkStyle(WpfColor Color, double WidthDip);

    private static readonly WpfColor[] Palette =
    {
        WpfColor.FromRgb(0xFF, 0x3B, 0x30), WpfColor.FromRgb(0xFF, 0xCC, 0x00), WpfColor.FromRgb(0x34, 0xC7, 0x59),
        WpfColor.FromRgb(0x00, 0x7A, 0xFF), WpfColor.FromRgb(0xFF, 0xFF, 0xFF), WpfColor.FromRgb(0x00, 0x00, 0x00),
    };
    private static readonly double[] InkWidthsDip = { 2, 3.5, 6 };
    private static readonly int[] MosaicBlocksFramePx = { 10, 16, 28 };
    private static readonly double[] EraserRadiiDip = { 6, 12, 20 };

    private readonly HdrFrame _frame;
    private readonly IReadOnlyList<DetectedWindow> _windows;
    private readonly RECT _monitor;
    private readonly float _sdrWhiteScale;
    private readonly string _saveDirectory;
    private readonly string _suggestedFileName;
    private readonly ChromeTheme _chrome;

    private readonly Canvas _canvas;
    private readonly WpfPath _dim;
    private readonly WpfRectangle _border;
    private readonly Border _sizeLabel;
    private readonly TextBlock _sizeText;
    private readonly Border _hint;
    private readonly Border _toolbar;
    private readonly Border _optionsPopup;
    private readonly StackPanel _optionsRow;
    private readonly List<(Tool Tool, ToggleButton Button)> _toolButtons = new();

    private const int MagnifierSourcePixels = 13; // odd, so the cursor pixel is centered
    private const int MagnifierScale = 10;

    private readonly byte[] _previewBgra;
    private readonly WriteableBitmap _annotationBitmap;
    private readonly WpfImage _annotationImage;
    private readonly WpfEllipse _eraserCursor;
    private readonly HdrBackdropWindow? _backdrop;
    private readonly Border _magnifier;
    private readonly WriteableBitmap _magnifierBitmap;
    private readonly byte[] _magnifierPixels = new byte[MagnifierSourcePixels * MagnifierSourcePixels * 4];
    private readonly TextBlock _magnifierPos;
    private readonly TextBlock _magnifierColor;
    private readonly TextBlock _magnifierHint;
    private readonly DispatcherTimer _hintReset = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private bool _colorAsHex;
    private (byte R, byte G, byte B) _pickedColor;
    // Mutated in place on mouse moves; rebuilding the geometry each move causes churn.
    private readonly RectangleGeometry _dimFullGeometry = new(Rect.Empty);
    private readonly RectangleGeometry _dimHoleGeometry = new(Rect.Empty);

    private OverlayMode _mode = OverlayMode.Selecting;
    private Tool _tool = Tool.None;
    private ShapeKind _shapeKind = ShapeKind.Rect;
    private readonly Dictionary<Tool, InkStyle> _inkStyles = new()
    {
        [Tool.Pen] = new InkStyle(WpfColor.FromRgb(0xFF, 0x3B, 0x30), 3.5),
        [Tool.Arrow] = new InkStyle(WpfColor.FromRgb(0xFF, 0x3B, 0x30), 3.5),
        [Tool.Shape] = new InkStyle(WpfColor.FromRgb(0xFF, 0x3B, 0x30), 3.5),
    };
    private int _mosaicBlockFramePx = 16;
    private double _eraserRadiusDip = 12;

    private Rect _imageBounds;
    private WpfPoint _mouseDownPoint;
    private bool _mousePressed;
    private bool _dragging;
    private Int32Rect? _targetFrameRect;
    private Int32Rect _selection;
    private ResizeAnchor _dragAnchor;
    private bool _movingSelection;
    private Int32Rect _dragStartSelection;
    private readonly List<WpfRectangle> _handles = new();

    private readonly List<Annotation> _operations = new();
    private UIElement? _liveElement;
    private List<WpfPoint>? _livePoints;
    private List<WpfPoint>? _eraserFramePoints;
    private WpfPoint _eraserLastFramePoint;

    private OverlayCaptureResult? _result;

    // ---- scrolling (long screenshot) state ----
    private ScrollCaptureSession? _scrollSession;
    private DispatcherTimer? _scrollTimer;
    private nint _scrollTargetHwnd;
    private Int32Rect _scrollRegionInWindow;
    private readonly List<Half[]> _scrollSegments = new();
    private int _scrollTotalRows;
    private int _scrollWidth;
    private byte[]? _scrollLastGray;
    private int _scrollGrayWidth;
    private int _scrollGrayHeight;
    private int _scrollNoMove;
    private int _scrollBadMatch;
    private int _scrollNullFrames;
    private bool _scrollFinished;
    private bool _scrollBusy;
    private Border? _scrollPanel;
    private TextBlock? _scrollStatus;
    private Border? _toast;
    private TextBlock? _toastText;
    private DispatcherTimer? _toastTimer;

    private CaptureOverlayWindow(HdrFrame frame, IReadOnlyList<DetectedWindow> windows, RECT monitor,
        float sdrWhiteScale, string saveDirectory, string suggestedFileName, ChromeTheme chrome)
    {
        _frame = frame;
        _windows = windows;
        _monitor = monitor;
        _sdrWhiteScale = sdrWhiteScale > 0 ? sdrWhiteScale : 1f;
        _saveDirectory = saveDirectory;
        _suggestedFileName = suggestedFileName;
        _chrome = chrome;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        Topmost = true;
        Cursor = Cursors.Cross;
        Title = "Kirari";
        Left = 0;
        Top = 0;

        // Heavy CPU work runs before the backdrop appears, so the frozen frame is never on
        // screen while the overlay is still unable to accept input.
        _previewBgra = CreatePreviewPixels(frame, _sdrWhiteScale);

        // True HDR preview: on an advanced-color display, present the scRGB frame through an
        // FP16 backdrop window (pixel-for-pixel the frozen desktop, HDR highlights included)
        // and make this overlay a transparent UI layer above it. Otherwise fall back to the
        // in-window SDR-referenced preview. The coordinate mapping requires the frame to
        // cover the monitor exactly.
        if (frame.Display?.HdrActive == true && frame.Width == monitor.Width && frame.Height == monitor.Height)
            _backdrop = HdrBackdropWindow.TryCreate(frame, monitor);
        try
        {
        if (_backdrop is not null)
        {
            // Transparency comes from DWM frame extension (OnSourceInitialized), NOT from
            // AllowsTransparency: layered WPF windows readback the full window GPU->CPU every
            // frame, which at 4K adds several frames of drag latency.
            Background = Brushes.Transparent;
        }
        else
        {
            Background = Brushes.Black;
        }

        WpfImage? image = null;
        if (_backdrop is null)
        {
            var previewBitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
            previewBitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), _previewBgra, frame.Width * 4, 0);
            image = new WpfImage { Source = previewBitmap, Stretch = Stretch.Fill, SnapsToDevicePixels = true };
        }

        _annotationBitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Pbgra32, null);
        _annotationImage = new WpfImage
        {
            Source = _annotationBitmap,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };

        // Over the backdrop the dim is alpha-blended by DWM in linear scRGB space, which reads
        // visibly weaker than WPF's gamma-space blend; compensate with a higher alpha there.
        _dim = new WpfPath
        {
            Fill = new SolidColorBrush(WpfColor.FromArgb(_backdrop is not null ? (byte)190 : (byte)120, 0, 0, 0)),
            IsHitTestVisible = false,
        };
        var dimGroup = new GeometryGroup { FillRule = FillRule.EvenOdd };
        dimGroup.Children.Add(_dimFullGeometry);
        dimGroup.Children.Add(_dimHoleGeometry);
        _dim.Data = dimGroup;
        // In HDR mode the dim is baked into the backdrop's presented frames instead.
        if (_backdrop is not null)
            _dim.Visibility = Visibility.Collapsed;
        _border = new WpfRectangle
        {
            Stroke = new SolidColorBrush(WpfColor.FromRgb(40, 160, 255)),
            StrokeThickness = 2,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _sizeText = new TextBlock { Foreground = _chrome.Text, FontSize = 12 };
        _sizeLabel = new Border
        {
            Background = _chrome.PanelBg,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 3, 6, 3),
            Child = _sizeText,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _eraserCursor = new WpfEllipse
        {
            Stroke = _chrome.Text,
            StrokeThickness = 1.5,
            Fill = new SolidColorBrush(WpfColor.FromArgb(30, 255, 255, 255)),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 0.9 },
        };

        // Alpha 1 instead of 0: on a transparent (layered) window, fully transparent pixels
        // are click-through, which would drop mouse input inside the undimmed selection hole.
        _magnifierBitmap = new WriteableBitmap(MagnifierSourcePixels, MagnifierSourcePixels, 96, 96, PixelFormats.Bgra32, null);
        (_magnifier, _magnifierPos, _magnifierColor, _magnifierHint) = BuildMagnifier(_magnifierBitmap);
        _hintReset.Tick += (_, _) => { _magnifierHint.Text = DefaultMagnifierHint; _hintReset.Stop(); };

        _canvas = new Canvas { Background = new SolidColorBrush(WpfColor.FromArgb(1, 0, 0, 0)) };
        _canvas.Children.Add(_dim);
        _canvas.Children.Add(_border);
        for (var i = 0; i < 8; i++)
        {
            var handle = new WpfRectangle
            {
                Width = 8,
                Height = 8,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(WpfColor.FromRgb(40, 160, 255)),
                StrokeThickness = 1,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };
            _handles.Add(handle);
            _canvas.Children.Add(handle);
        }
        _canvas.Children.Add(_sizeLabel);
        _canvas.Children.Add(_eraserCursor);
        _canvas.Children.Add(_magnifier);
        _canvas.MouseMove += OnMouseMove;
        _canvas.MouseLeftButtonDown += OnMouseLeftButtonDown;
        _canvas.MouseLeftButtonUp += OnMouseLeftButtonUp;
        _canvas.MouseRightButtonUp += (_, _) => Cancel();

        _optionsRow = new StackPanel { Orientation = Orientation.Horizontal };
        _optionsPopup = new Border
        {
            Background = _chrome.PanelBg,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 4, 6, 4),
            Child = _optionsRow,
            Visibility = Visibility.Collapsed,
            Cursor = Cursors.Arrow,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.5 },
        };
        _toolbar = BuildToolbar();
        _toolbar.Visibility = Visibility.Collapsed;
        _canvas.Children.Add(_toolbar);
        _canvas.Children.Add(_optionsPopup);

        _hint = new Border
        {
            Background = _chrome.PanelBg,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 7, 12, 7),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 24, 0, 0),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "移动鼠标高亮窗口  ·  单击截取窗口  ·  拖拽框选任意区域  ·  Esc / 右键取消",
                Foreground = _chrome.Text,
                FontSize = 14,
            },
        };

        var root = new Grid();
        if (image is not null)
            root.Children.Add(image);
        root.Children.Add(_annotationImage);
        root.Children.Add(_canvas);
        root.Children.Add(_hint);
        Content = root;

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => { UpdateImageBounds(); Activate(); Focus(); };
        SizeChanged += (_, _) => { UpdateImageBounds(); RefreshVisuals(); };
        PreviewKeyDown += OnPreviewKeyDown;
        // Re-slot on every activation: activation raises only this window within the topmost
        // band, which could otherwise sandwich another topmost window above the backdrop.
        Activated += (_, _) => SlotBackdropBeneath();
        Closed += (_, _) =>
        {
            _scrollTimer?.Stop();
            _scrollSession?.Dispose();
            _backdrop?.Dispose();
        };
        }
        catch
        {
            // The backdrop is already visible; never leave it orphaned covering the monitor.
            _backdrop?.Dispose();
            throw;
        }
    }

    public static OverlayCaptureResult? Capture(HdrFrame frame, IReadOnlyList<DetectedWindow> windows, RECT monitor,
        float sdrWhiteScale, string saveDirectory, string suggestedFileName, ChromeTheme chrome)
    {
        var window = new CaptureOverlayWindow(frame, windows, monitor, sdrWhiteScale, saveDirectory, suggestedFileName, chrome);
        try
        {
            return window.ShowDialog() == true ? window._result : null;
        }
        finally
        {
            // Normally disposed by Closed; this covers exceptions escaping the dialog pump.
            window._backdrop?.Dispose();
        }
    }

    // ---------------------------------------------------------------- window plumbing

    private const int WmDpiChanged = 0x02E0;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(OverlayHook);
        if (_backdrop is not null && source is not null)
        {
            // GPU-composited per-pixel transparency without the layered-window readback.
            var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
        }
        // Show the pre-presented backdrop and the overlay back to back so they land in the
        // same composition beat — showing the backdrop earlier flashes an undimmed frame.
        _backdrop?.Show();
        NativeMethods.SetWindowPos(hwnd, 0, _monitor.Left, _monitor.Top, _monitor.Width, _monitor.Height,
            NativeMethods.SwpNoZorder | NativeMethods.SwpShowWindow);
        NativeMethods.SetForegroundWindow(hwnd);
        SlotBackdropBeneath();
    }

    private nint OverlayHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmDpiChanged)
        {
            // Crossing a DPI boundary makes WPF apply the suggested rect, which would desync
            // this window from the monitor/backdrop; keep the monitor rect and suppress it.
            NativeMethods.SetWindowPos(hwnd, 0, _monitor.Left, _monitor.Top, _monitor.Width, _monitor.Height,
                NativeMethods.SwpNoZorder | NativeMethods.SwpNoActivate);
            handled = true;
        }
        return 0;
    }

    /// <summary>Places the HDR backdrop directly beneath this overlay so nothing sits between them.</summary>
    private void SlotBackdropBeneath()
    {
        if (_backdrop is null) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0)
            NativeMethods.SetWindowPos(_backdrop.Hwnd, hwnd, 0, 0, 0, 0,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
    }

    private void OnPreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancel();
            e.Handled = true;
        }
        else if (_mode == OverlayMode.Selecting && e.Key == Key.C)
        {
            CopyPickedColor();
            e.Handled = true;
        }
        else if (_mode == OverlayMode.Selecting && e.Key is Key.LeftShift or Key.RightShift && !e.IsRepeat)
        {
            _colorAsHex = !_colorAsHex;
            _magnifierColor.Text = FormatPickedColor();
            e.Handled = true;
        }
        else if (_mode == OverlayMode.Editing && e.Key == Key.Enter)
        {
            FinishToClipboard();
            e.Handled = true;
        }
        else if (_mode == OverlayMode.Editing && e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Undo();
            e.Handled = true;
        }
        else if (_mode == OverlayMode.Editing && e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SaveAs();
            e.Handled = true;
        }
    }

    private void Cancel() => DialogResult = false;

    // ---------------------------------------------------------------- mouse handling

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_mode == OverlayMode.Scrolling) return;
        if (_toolbar.IsMouseOver || _optionsPopup.IsMouseOver) return;
        UpdateImageBounds();
        var position = e.GetPosition(_canvas);

        if (_mode == OverlayMode.Selecting)
        {
            _mouseDownPoint = position;
            _mousePressed = true;
            _dragging = false;
            _canvas.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 2 && _tool == Tool.None)
        {
            FinishToClipboard();
            e.Handled = true;
            return;
        }
        if (_tool == Tool.None)
        {
            // With no tool active the selection itself is adjustable: drag inside to move it,
            // drag an edge or corner to resize. Annotations stay glued to the image content.
            var anchor = HitTestSelectionEdges(position);
            if (anchor != ResizeAnchor.None || FrameToOverlay(_selection).Contains(position))
            {
                _dragAnchor = anchor;
                _movingSelection = anchor == ResizeAnchor.None;
                _dragStartSelection = _selection;
                _mouseDownPoint = position;
                _mousePressed = true;
                _canvas.CaptureMouse();
                e.Handled = true;
            }
            return;
        }
        if (_tool == Tool.Eraser)
        {
            _mousePressed = true;
            var framePoint = OverlayPointToFrame(ClampToSelection(position));
            _eraserFramePoints = new List<WpfPoint> { framePoint };
            _eraserLastFramePoint = framePoint;
            EraseCircleCore(framePoint, EraserRadiusFramePx());
            _canvas.CaptureMouse();
            e.Handled = true;
            return;
        }
        if (_tool is Tool.Pen or Tool.Arrow or Tool.Shape or Tool.Mosaic)
        {
            _mouseDownPoint = ClampToSelection(position);
            _mousePressed = true;
            _livePoints = _tool == Tool.Pen ? new List<WpfPoint> { _mouseDownPoint } : null;
            _canvas.CaptureMouse();
            e.Handled = true;
        }
    }

    private void OnMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (_mode == OverlayMode.Scrolling) return;
        var position = e.GetPosition(_canvas);

        if (_mode == OverlayMode.Selecting)
        {
            UpdateMagnifier(position);
            if (_mousePressed)
            {
                if (!_dragging &&
                    (Math.Abs(position.X - _mouseDownPoint.X) > DragThreshold ||
                     Math.Abs(position.Y - _mouseDownPoint.Y) > DragThreshold))
                    _dragging = true;
                if (_dragging)
                {
                    _targetFrameRect = OverlayToFrame(ClampToImage(NormalizeRect(_mouseDownPoint, position)));
                    RefreshVisuals();
                }
                return;
            }
            _targetFrameRect = HitTestWindow(OverlayPointToFrame(position));
            RefreshVisuals();
            return;
        }

        if (_tool == Tool.Eraser)
        {
            // Hide the brush ring while over the toolbar, where the normal cursor shows.
            if (_toolbar.IsMouseOver || _optionsPopup.IsMouseOver)
                _eraserCursor.Visibility = Visibility.Collapsed;
            else
                UpdateEraserCursor(position);
        }
        else if (_tool == Tool.None && !_mousePressed)
        {
            var anchor = HitTestSelectionEdges(position);
            Cursor = anchor != ResizeAnchor.None ? CursorForAnchor(anchor)
                : FrameToOverlay(_selection).Contains(position) ? Cursors.SizeAll
                : Cursors.Arrow;
        }

        if (!_mousePressed) return;

        if (_movingSelection || _dragAnchor != ResizeAnchor.None)
        {
            HandleSelectionDrag(position);
            return;
        }

        if (_tool == Tool.Eraser && _eraserFramePoints is not null)
        {
            var framePoint = OverlayPointToFrame(ClampToSelection(position));
            EraseSegmentCore(_eraserLastFramePoint, framePoint, EraserRadiusFramePx());
            _eraserFramePoints.Add(framePoint);
            _eraserLastFramePoint = framePoint;
            return;
        }
        UpdateLiveDrawing(ClampToSelection(position));
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_mode == OverlayMode.Scrolling) return;
        if (!_mousePressed) return;
        _mousePressed = false;
        _canvas.ReleaseMouseCapture();
        var position = e.GetPosition(_canvas);

        if (_mode == OverlayMode.Selecting)
        {
            Int32Rect? chosen = _dragging
                ? OverlayToFrame(ClampToImage(NormalizeRect(_mouseDownPoint, position)))
                : _targetFrameRect;
            if (chosen is { Width: >= 2, Height: >= 2 } rect)
                EnterEditMode(rect);
            e.Handled = true;
            return;
        }

        if (_movingSelection || _dragAnchor != ResizeAnchor.None)
        {
            _movingSelection = false;
            _dragAnchor = ResizeAnchor.None;
            e.Handled = true;
            return;
        }

        if (_tool == Tool.Eraser)
        {
            if (_eraserFramePoints is { Count: > 0 })
                _operations.Add(new EraserAnnotation(_eraserFramePoints, EraserRadiusFramePx()));
            _eraserFramePoints = null;
            e.Handled = true;
            return;
        }

        CommitLiveDrawing(ClampToSelection(position));
        e.Handled = true;
    }

    // ---------------------------------------------------------------- OCR

    private async void RunOcr()
    {
        if (_mode != OverlayMode.Editing) return;
        var selection = _selection;
        var crop = new byte[selection.Width * selection.Height * 4];
        for (var y = 0; y < selection.Height; y++)
            Buffer.BlockCopy(_previewBgra, ((selection.Y + y) * _frame.Width + selection.X) * 4,
                crop, y * selection.Width * 4, selection.Width * 4);
        ShowToast("识别中…", sticky: true);
        try
        {
            var text = await OcrService.RecognizeAsync(crop, selection.Width, selection.Height);
            if (!IsVisible) return;
            if (string.IsNullOrWhiteSpace(text))
            {
                ShowToast("未识别到文字");
                return;
            }
            try
            {
                Clipboard.SetText(text);
            }
            catch
            {
                ShowToast("剪贴板被占用，复制失败");
                return;
            }
            // Copied successfully: abort the capture outright — no completion pipeline.
            DialogResult = false;
        }
        catch (Exception ex)
        {
            if (IsVisible) ShowToast($"识别失败：{ex.Message}");
        }
    }

    private void ShowToast(string message, bool sticky = false)
    {
        if (_toast is null)
        {
            _toastText = new TextBlock { Foreground = _chrome.Text, FontSize = 13 };
            _toast = new Border
            {
                Background = _chrome.PanelBg,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 8, 14, 8),
                Child = _toastText,
                IsHitTestVisible = false,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.5 },
            };
            _canvas.Children.Add(_toast);
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
            _toastTimer.Tick += (_, _) =>
            {
                _toast!.Visibility = Visibility.Collapsed;
                _toastTimer!.Stop();
            };
        }
        _toastText!.Text = message;
        _toast.Visibility = Visibility.Visible;
        _toast.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var selection = FrameToOverlay(_selection);
        var left = Math.Clamp(selection.Left + (selection.Width - _toast.DesiredSize.Width) / 2, 8,
            Math.Max(8, ActualWidth - _toast.DesiredSize.Width - 8));
        var top = Math.Max(8, selection.Top + 12);
        Canvas.SetLeft(_toast, left);
        Canvas.SetTop(_toast, top);
        _toastTimer!.Stop();
        if (!sticky) _toastTimer.Start();
    }

    // ---------------------------------------------------------------- scrolling (long screenshot)

    private void StartScrollCapture()
    {
        if (_mode != OverlayMode.Editing) return;
        var centerX = _selection.X + _selection.Width / 2;
        var centerY = _selection.Y + _selection.Height / 2;
        DetectedWindow? target = null;
        foreach (var window in _windows)
        {
            var rect = window.FrameRect;
            if (centerX >= rect.X && centerX < rect.X + rect.Width && centerY >= rect.Y && centerY < rect.Y + rect.Height)
            {
                target = window;
                break;
            }
        }
        if (target is null)
        {
            MessageBox.Show(this, "选区下没有可滚动的目标窗口。", "长截图", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ScrollCaptureSession session;
        try
        {
            session = new ScrollCaptureSession(target.Value.Hwnd);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法捕获目标窗口：{ex.Message}", "长截图", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Selection (monitor-frame px) -> target-window content px.
        var screenX = _monitor.Left + _selection.X;
        var screenY = _monitor.Top + _selection.Y;
        var regionX = Math.Clamp(screenX - target.Value.ScreenBounds.Left, 0, Math.Max(0, session.ItemSize.Width - 1));
        var regionY = Math.Clamp(screenY - target.Value.ScreenBounds.Top, 0, Math.Max(0, session.ItemSize.Height - 1));
        var regionW = Math.Clamp(_selection.Width, 1, session.ItemSize.Width - regionX);
        var regionH = Math.Clamp(_selection.Height, 1, session.ItemSize.Height - regionY);
        if (regionW < 50 || regionH < 80)
        {
            session.Dispose();
            MessageBox.Show(this, "选区太小，无法进行滚动拼接。", "长截图", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var baseline = session.TryGrabLatest(TimeSpan.FromSeconds(1.5));
        if (baseline is null)
        {
            session.Dispose();
            MessageBox.Show(this, "未能从目标窗口获取画面。", "长截图", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectTool(Tool.None);
        _mode = OverlayMode.Scrolling;
        _scrollSession = session;
        _scrollTargetHwnd = target.Value.Hwnd;
        _scrollRegionInWindow = new Int32Rect(regionX, regionY, regionW, regionH);
        _scrollSegments.Clear();
        _scrollTotalRows = 0;
        _scrollWidth = regionW;
        _scrollNoMove = 0;
        _scrollBadMatch = 0;
        _scrollNullFrames = 0;
        _scrollFinished = false;
        _scrollLastGray = null;
        _toolbar.Visibility = Visibility.Collapsed;
        _optionsPopup.Visibility = Visibility.Collapsed;
        Cursor = Cursors.Arrow;

        var crop = AnnotationBaker.Crop(baseline, _scrollRegionInWindow);
        AppendScrollSegment(crop.Pixels, crop.Height);
        _scrollLastGray = MakeGray(crop, out _scrollGrayWidth, out _scrollGrayHeight);
        ShowScrollPanel();

        _scrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _scrollTimer.Tick += (_, _) => ScrollTick();
        _scrollTimer.Start();
    }

    private void ScrollTick()
    {
        if (_scrollBusy || _scrollSession is null || _scrollFinished) return;
        _scrollBusy = true;
        try
        {
            var full = _scrollSession.TryGrabLatest(TimeSpan.FromMilliseconds(160));
            if (full is null)
            {
                // WGC is dirty-driven: a static (page bottom), minimized, resized or closed
                // target produces no frames at all — the null path needs its own terminator.
                if (++_scrollNullFrames >= 8) { FinishScroll(); return; }
            }
            else
            {
                _scrollNullFrames = 0;
                var crop = AnnotationBaker.Crop(full, _scrollRegionInWindow);
                var gray = MakeGray(crop, out var grayWidth, out var grayHeight);
                var (delta, quality) = MatchScroll(_scrollLastGray!, gray, grayWidth, grayHeight);
                if (quality > 12)
                {
                    // Unreliable match (animation/video/blank region); skip this frame.
                    if (++_scrollBadMatch >= 10) { FinishScroll(); return; }
                }
                else if (delta == 0)
                {
                    if (++_scrollNoMove >= 5) { FinishScroll(); return; }
                }
                else
                {
                    _scrollNoMove = 0;
                    _scrollBadMatch = 0;
                    var newRows = Math.Min(delta, crop.Height);
                    var segment = new Half[newRows * crop.Width * 4];
                    Array.Copy(crop.Pixels, (crop.Height - newRows) * crop.Width * 4, segment, 0, segment.Length);
                    AppendScrollSegment(segment, newRows);
                    _scrollLastGray = gray;
                    if (_scrollStatus is not null)
                        _scrollStatus.Text = $"滚动截取中…  已拼接 {_scrollTotalRows} px";
                }
                // Memory cap: HDR rows are 8 bytes per pixel.
                if ((long)_scrollTotalRows * _scrollWidth * 8 > 512L * 1024 * 1024 || _scrollTotalRows > 30000)
                {
                    FinishScroll();
                    return;
                }
            }
            // Small selections can only detect small advances; scale the wheel step down.
            NativeMethods.SendMouseWheel(_scrollTargetHwnd,
                _monitor.Left + _selection.X + _selection.Width / 2,
                _monitor.Top + _selection.Y + _selection.Height / 2,
                notches: _selection.Height < 300 ? 1 : 2);
        }
        catch
        {
            // Device loss or readback failure mid-session: salvage what was stitched.
            try { FinishScroll(); } catch { DialogResult = false; }
        }
        finally
        {
            _scrollBusy = false;
        }
    }

    private void AppendScrollSegment(Half[] rows, int rowCount)
    {
        _scrollSegments.Add(rows);
        _scrollTotalRows += rowCount;
    }

    private byte[] MakeGray(HdrFrame crop, out int grayWidth, out int grayHeight)
    {
        // Full resolution: a decimated matcher can only measure even scroll advances, which
        // puts a 1-px seam at every junction when the target scrolls an odd pixel count.
        grayWidth = crop.Width;
        grayHeight = crop.Height;
        var gray = new byte[grayWidth * grayHeight];
        var invScale = 1f / _sdrWhiteScale;
        for (var y = 0; y < grayHeight; y++)
        {
            for (var x = 0; x < grayWidth; x++)
            {
                var p = (y * crop.Width + x) * 4;
                var luma = ((float)crop.Pixels[p] + 2f * (float)crop.Pixels[p + 1] + (float)crop.Pixels[p + 2]) * 0.25f * invScale;
                gray[y * grayWidth + x] = (byte)Math.Clamp(luma * 255f, 0f, 255f);
            }
        }
        return gray;
    }

    /// <summary>
    /// Finds the scroll advance between consecutive frames: content moves up, so new-frame row
    /// y matches last-frame row y+d. The top 20% of the region is excluded from scoring to
    /// tolerate fixed headers; a minimum overlap and a texture guard prevent blank content
    /// from producing spurious perfect matches. Coarse (step 2) then fine (step 1) search.
    /// Returns (bestDelta, bestScore) — lower score is better.
    /// </summary>
    private static (int Delta, float Score) MatchScroll(byte[] last, byte[] current, int width, int height)
    {
        var bandTop = height / 5;
        var minOverlap = Math.Max(24, height / 4);
        var maxDelta = height - bandTop - minOverlap;
        if (maxDelta < 2) return (0, 999);

        // Texture guard: blank bands match every offset with zero error.
        long edgeSum = 0;
        var edgeCount = 0;
        for (var y = bandTop; y < height - 1; y += 4)
        {
            var row = y * width;
            for (var x = 0; x < width; x += 4)
            {
                edgeSum += Math.Abs(current[row + x] - current[row + width + x]);
                edgeCount++;
            }
        }
        if (edgeCount == 0 || (float)edgeSum / edgeCount < 1f) return (0, 999);

        var (coarse, _) = BestDelta(last, current, width, height, bandTop, 0, maxDelta, 2);
        return BestDelta(last, current, width, height, bandTop, Math.Max(0, coarse - 2), Math.Min(maxDelta, coarse + 2), 1);
    }

    private static (int Delta, float Score) BestDelta(byte[] last, byte[] current, int width, int height,
        int bandTop, int from, int to, int step)
    {
        var bestDelta = from;
        var bestScore = float.MaxValue;
        for (var delta = from; delta <= to; delta += step)
        {
            long sum = 0;
            var count = 0;
            for (var y = bandTop; y < height - delta; y += 3)
            {
                var lastRow = (y + delta) * width;
                var currentRow = y * width;
                for (var x = 0; x < width; x += 3)
                {
                    sum += Math.Abs(last[lastRow + x] - current[currentRow + x]);
                    count++;
                }
            }
            if (count == 0) continue;
            var score = (float)sum / count;
            if (score < bestScore)
            {
                bestScore = score;
                bestDelta = delta;
            }
        }
        return (bestDelta, bestScore);
    }

    private void ShowScrollPanel()
    {
        if (_scrollPanel is null)
        {
            _scrollStatus = new TextBlock { Foreground = _chrome.Text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            var stop = new Button
            {
                Content = "结束并保存",
                Margin = new Thickness(12, 0, 0, 0),
                Padding = new Thickness(12, 4, 12, 4),
            };
            stop.Click += (_, _) => FinishScroll();
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(_scrollStatus);
            panel.Children.Add(stop);
            _scrollPanel = new Border
            {
                Background = _chrome.PanelBg,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Child = panel,
                Cursor = Cursors.Arrow,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.5 },
            };
            _canvas.Children.Add(_scrollPanel);
        }
        _scrollStatus!.Text = "滚动截取中…";
        _scrollPanel.Visibility = Visibility.Visible;
        _scrollPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var selection = FrameToOverlay(_selection);
        var left = Math.Clamp(selection.Left + (selection.Width - _scrollPanel.DesiredSize.Width) / 2, 8, Math.Max(8, ActualWidth - _scrollPanel.DesiredSize.Width - 8));
        var top = selection.Top - _scrollPanel.DesiredSize.Height - 10;
        if (top < 8) top = selection.Top + 10;
        Canvas.SetLeft(_scrollPanel, left);
        Canvas.SetTop(_scrollPanel, top);
    }

    private void FinishScroll()
    {
        if (_scrollFinished) return;
        _scrollFinished = true;
        _scrollTimer?.Stop();
        _scrollSession?.Dispose();
        _scrollSession = null;
        if (_scrollTotalRows <= 0) { DialogResult = false; return; }

        var pixels = new Half[(long)_scrollTotalRows * _scrollWidth * 4];
        var offset = 0L;
        foreach (var segment in _scrollSegments)
        {
            Array.Copy(segment, 0, pixels, offset, segment.Length);
            offset += segment.Length;
        }
        var stitched = new HdrFrame { Width = _scrollWidth, Height = _scrollTotalRows, Pixels = pixels, Display = _frame.Display };
        var path = System.IO.Path.Combine(_saveDirectory, $"Long_{_suggestedFileName}");
        _result = new OverlayCaptureResult(stitched, path, CopyToClipboard: true);
        DialogResult = true;
    }

    // ---------------------------------------------------------------- selection move/resize

    private ResizeAnchor HitTestSelectionEdges(WpfPoint position)
    {
        const double near = 6;
        var rect = FrameToOverlay(_selection);
        if (position.X < rect.Left - near || position.X > rect.Right + near ||
            position.Y < rect.Top - near || position.Y > rect.Bottom + near)
            return ResizeAnchor.None;
        var anchor = ResizeAnchor.None;
        if (Math.Abs(position.X - rect.Left) <= near) anchor |= ResizeAnchor.Left;
        else if (Math.Abs(position.X - rect.Right) <= near) anchor |= ResizeAnchor.Right;
        if (Math.Abs(position.Y - rect.Top) <= near) anchor |= ResizeAnchor.Top;
        else if (Math.Abs(position.Y - rect.Bottom) <= near) anchor |= ResizeAnchor.Bottom;
        return anchor;
    }

    private static Cursor CursorForAnchor(ResizeAnchor anchor)
    {
        var horizontal = (anchor & (ResizeAnchor.Left | ResizeAnchor.Right)) != 0;
        var vertical = (anchor & (ResizeAnchor.Top | ResizeAnchor.Bottom)) != 0;
        if (horizontal && vertical)
        {
            var diagonal = anchor is (ResizeAnchor.Left | ResizeAnchor.Top) or (ResizeAnchor.Right | ResizeAnchor.Bottom);
            return diagonal ? Cursors.SizeNWSE : Cursors.SizeNESW;
        }
        return horizontal ? Cursors.SizeWE : Cursors.SizeNS;
    }

    private void HandleSelectionDrag(WpfPoint position)
    {
        const int minSize = 8;
        var deltaX = (position.X - _mouseDownPoint.X) / _imageBounds.Width * _frame.Width;
        var deltaY = (position.Y - _mouseDownPoint.Y) / _imageBounds.Height * _frame.Height;
        var start = _dragStartSelection;
        int left = start.X, top = start.Y, right = start.X + start.Width, bottom = start.Y + start.Height;

        if (_movingSelection)
        {
            left = (int)Math.Round(Math.Clamp(start.X + deltaX, 0, _frame.Width - start.Width));
            top = (int)Math.Round(Math.Clamp(start.Y + deltaY, 0, _frame.Height - start.Height));
            right = left + start.Width;
            bottom = top + start.Height;
        }
        else
        {
            if (_dragAnchor.HasFlag(ResizeAnchor.Left))
                left = (int)Math.Round(Math.Clamp(start.X + deltaX, 0, right - minSize));
            if (_dragAnchor.HasFlag(ResizeAnchor.Right))
                right = (int)Math.Round(Math.Clamp(start.X + start.Width + deltaX, left + minSize, _frame.Width));
            if (_dragAnchor.HasFlag(ResizeAnchor.Top))
                top = (int)Math.Round(Math.Clamp(start.Y + deltaY, 0, bottom - minSize));
            if (_dragAnchor.HasFlag(ResizeAnchor.Bottom))
                bottom = (int)Math.Round(Math.Clamp(start.Y + start.Height + deltaY, top + minSize, _frame.Height));
        }
        ApplySelectionBounds(new Int32Rect(left, top, right - left, bottom - top));
    }

    private void ApplySelectionBounds(Int32Rect bounds)
    {
        _selection = bounds;
        _targetFrameRect = bounds;
        RefreshVisuals();
        _annotationImage.Clip = new RectangleGeometry(FrameToOverlay(bounds));
        PositionToolbar();
        PositionOptionsPopup();
    }

    // ---------------------------------------------------------------- edit mode

    private void EnterEditMode(Int32Rect selection)
    {
        _mode = OverlayMode.Editing;
        _selection = selection;
        _targetFrameRect = selection;
        _hint.Visibility = Visibility.Collapsed;
        _magnifier.Visibility = Visibility.Collapsed;
        Cursor = Cursors.Arrow;
        RefreshVisuals();

        _annotationImage.Clip = new RectangleGeometry(FrameToOverlay(selection));
        _annotationImage.Visibility = Visibility.Visible;

        // Visible before positioning: a collapsed element measures to zero size. The main
        // toolbar is positioned exactly once and never moves afterwards; the sub-toolbar is
        // a separate popup positioned relative to it.
        _toolbar.Visibility = Visibility.Visible;
        PositionToolbar();
    }

    private void PositionToolbar()
    {
        var selection = FrameToOverlay(_selection);
        _toolbar.InvalidateMeasure();
        _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = _toolbar.DesiredSize.Width;
        var height = _toolbar.DesiredSize.Height;

        // Right-aligned to the selection's right edge; below the selection, falling back to
        // the selection's inside bottom edge when the screen bottom is reached.
        var left = Math.Max(8, Math.Min(selection.Right, ActualWidth - 8) - width);
        var top = selection.Bottom + 10;
        if (top + height > ActualHeight - 8)
            top = selection.Bottom - height - 10;
        top = Math.Clamp(top, 8, Math.Max(8, ActualHeight - height - 8));
        Canvas.SetLeft(_toolbar, left);
        Canvas.SetTop(_toolbar, top);
    }

    private void PositionOptionsPopup()
    {
        if (_optionsPopup.Visibility != Visibility.Visible) return;
        _optionsPopup.InvalidateMeasure();
        _optionsPopup.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = _optionsPopup.DesiredSize.Width;
        var height = _optionsPopup.DesiredSize.Height;
        _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var toolbarLeft = Canvas.GetLeft(_toolbar);
        var toolbarTop = Canvas.GetTop(_toolbar);
        var toolbarHeight = _toolbar.DesiredSize.Height;

        // Left-aligned with the main toolbar; below it, popping above when the screen
        // bottom leaves no room. The main toolbar itself never moves.
        var left = Math.Clamp(toolbarLeft, 8, Math.Max(8, ActualWidth - width - 8));
        var top = toolbarTop + toolbarHeight + 6;
        if (top + height > ActualHeight - 8)
            top = toolbarTop - height - 6;
        top = Math.Max(8, top);
        Canvas.SetLeft(_optionsPopup, left);
        Canvas.SetTop(_optionsPopup, top);
    }

    private void FinishToClipboard() => Finish(null, copyToClipboard: true);

    private void SaveAs()
    {
        var extension = System.IO.Path.GetExtension(_suggestedFileName);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            InitialDirectory = _saveDirectory,
            FileName = _suggestedFileName,
            DefaultExt = extension,
            Filter = extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                ? "JPG (*.jpg)|*.jpg"
                : "PNG (*.png)|*.png",
        };
        if (dialog.ShowDialog(this) == true)
            Finish(dialog.FileName, copyToClipboard: false);
    }

    private void Finish(string? savePath, bool copyToClipboard)
    {
        var baked = AnnotationBaker.Bake(_frame, _selection, _operations, _sdrWhiteScale);
        _result = new OverlayCaptureResult(baked, savePath, copyToClipboard);
        DialogResult = true;
    }

    private void Undo()
    {
        if (_operations.Count == 0) return;
        _operations.RemoveAt(_operations.Count - 1);
        ReplayAll();
    }

    // ---------------------------------------------------------------- raster annotation layer (frame resolution)

    private void CommitOperation(Annotation operation)
    {
        _operations.Add(operation);
        RenderOperation(operation);
    }

    private void ReplayAll()
    {
        ClearAnnotationBitmap();
        foreach (var operation in _operations)
            RenderOperation(operation);
    }

    private unsafe void ClearAnnotationBitmap()
    {
        _annotationBitmap.Lock();
        var bytes = (long)_annotationBitmap.BackBufferStride * _frame.Height;
        System.Runtime.CompilerServices.Unsafe.InitBlockUnaligned((void*)_annotationBitmap.BackBuffer, 0, (uint)bytes);
        _annotationBitmap.AddDirtyRect(new Int32Rect(0, 0, _frame.Width, _frame.Height));
        _annotationBitmap.Unlock();
    }

    private void RenderOperation(Annotation operation)
    {
        switch (operation)
        {
            case MosaicAnnotation mosaic:
                RenderMosaicBlocks(mosaic);
                break;
            case EraserAnnotation eraser:
                EraseCircles(BuildEraserCenters(eraser.Points, eraser.Radius), eraser.Radius);
                break;
            default:
                RenderInk(operation);
                break;
        }
    }

    private void RenderInk(Annotation annotation)
    {
        // Render into a bounding-box-sized target, not a frame-sized one: a full 4K
        // RenderTargetBitmap per stroke commit causes a visible hitch.
        var bounds = InkBounds(annotation);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
            AnnotationBaker.DrawInk(context, annotation);
            context.Pop();
        }
        var rendered = new RenderTargetBitmap(bounds.Width, bounds.Height, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        var pixels = new byte[bounds.Width * bounds.Height * 4];
        rendered.CopyPixels(pixels, bounds.Width * 4, 0);
        BlitOver(bounds, pixels);
    }

    private Int32Rect InkBounds(Annotation annotation)
    {
        Rect rect = annotation switch
        {
            StrokeAnnotation stroke => BoundsOf(stroke.Points, stroke.Width + 2),
            ArrowAnnotation arrow => BoundsOf(new[] { arrow.From, arrow.To }, arrow.Width * 5 + 4),
            RectAnnotation r => Inflate(r.Bounds, r.Width + 2),
            EllipseAnnotation ellipse => Inflate(ellipse.Bounds, ellipse.Width + 2),
            _ => Rect.Empty,
        };
        if (rect.IsEmpty) return default;
        var left = Math.Clamp((int)Math.Floor(rect.Left), 0, _frame.Width);
        var top = Math.Clamp((int)Math.Floor(rect.Top), 0, _frame.Height);
        var right = Math.Clamp((int)Math.Ceiling(rect.Right), left, _frame.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(rect.Bottom), top, _frame.Height);
        return new Int32Rect(left, top, right - left, bottom - top);
    }

    private static Rect BoundsOf(IReadOnlyList<WpfPoint> points, double inflate)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var point in points)
        {
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }
        return Inflate(new Rect(new WpfPoint(minX, minY), new WpfPoint(maxX, maxY)), inflate);
    }

    private static Rect Inflate(Rect rect, double amount)
    {
        rect.Inflate(amount, amount);
        return rect;
    }

    private unsafe void BlitOver(Int32Rect bounds, byte[] source)
    {
        _annotationBitmap.Lock();
        var stride = _annotationBitmap.BackBufferStride;
        var buffer = (byte*)_annotationBitmap.BackBuffer;
        for (var y = 0; y < bounds.Height; y++)
        {
            var row = buffer + (bounds.Y + y) * stride + bounds.X * 4;
            var sourceRow = y * bounds.Width * 4;
            for (var x = 0; x < bounds.Width; x++)
            {
                var srcAlpha = source[sourceRow + x * 4 + 3];
                if (srcAlpha == 0) continue;
                var keep = (255 - srcAlpha) / 255f;
                var d = x * 4;
                // Premultiplied source-over.
                row[d] = (byte)Math.Min(255f, source[sourceRow + d] + row[d] * keep);
                row[d + 1] = (byte)Math.Min(255f, source[sourceRow + d + 1] + row[d + 1] * keep);
                row[d + 2] = (byte)Math.Min(255f, source[sourceRow + d + 2] + row[d + 2] * keep);
                row[d + 3] = (byte)Math.Min(255f, srcAlpha + row[d + 3] * keep);
            }
        }
        _annotationBitmap.AddDirtyRect(bounds);
        _annotationBitmap.Unlock();
    }

    private void RenderMosaicBlocks(MosaicAnnotation mosaic)
    {
        var bounds = mosaic.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var invScale = 1f / _sdrWhiteScale;
        // Snapshot the current annotation layer so the mosaic pixelates ink drawn before it,
        // mirroring the baker's chronological composite.
        var annotation = new byte[bounds.Width * bounds.Height * 4];
        _annotationBitmap.CopyPixels(bounds, annotation, bounds.Width * 4, 0);
        var pixels = new byte[bounds.Width * bounds.Height * 4];
        for (var blockY = 0; blockY < bounds.Height; blockY += mosaic.BlockSize)
        {
            for (var blockX = 0; blockX < bounds.Width; blockX += mosaic.BlockSize)
            {
                // Average the linear composite (frame + existing ink at SDR white level),
                // matching the baker's block statistics; tone-map only for display.
                double sumR = 0, sumG = 0, sumB = 0;
                var count = 0;
                var endX = Math.Min(blockX + mosaic.BlockSize, bounds.Width);
                var endY = Math.Min(blockY + mosaic.BlockSize, bounds.Height);
                for (var y = blockY; y < endY; y++)
                {
                    for (var x = blockX; x < endX; x++)
                    {
                        var p = ((bounds.Y + y) * _frame.Width + bounds.X + x) * 4;
                        var a = y * bounds.Width * 4 + x * 4;
                        var alpha = annotation[a + 3] / 255f;
                        var keep = 1f - alpha;
                        sumR += (float)_frame.Pixels[p] * keep + InkToLinear(annotation[a + 2], alpha);
                        sumG += (float)_frame.Pixels[p + 1] * keep + InkToLinear(annotation[a + 1], alpha);
                        sumB += (float)_frame.Pixels[p + 2] * keep + InkToLinear(annotation[a], alpha);
                        count++;
                    }
                }
                if (count == 0) continue;
                var avgB = ToSrgb((float)(sumB / count) * invScale);
                var avgG = ToSrgb((float)(sumG / count) * invScale);
                var avgR = ToSrgb((float)(sumR / count) * invScale);
                for (var y = blockY; y < endY; y++)
                {
                    for (var x = blockX; x < endX; x++)
                    {
                        var d = (y * bounds.Width + x) * 4;
                        pixels[d] = avgB;
                        pixels[d + 1] = avgG;
                        pixels[d + 2] = avgR;
                        pixels[d + 3] = 255;
                    }
                }
            }
        }
        BlitOver(bounds, pixels);
    }

    private double EraserRadiusFramePx() => Math.Max(1, _eraserRadiusDip * OverlayToFrameScale());

    private static List<WpfPoint> BuildEraserCenters(IReadOnlyList<WpfPoint> points, double radius)
    {
        var centers = new List<WpfPoint>();
        if (points.Count == 0) return centers;
        centers.Add(points[0]);
        for (var i = 1; i < points.Count; i++)
        {
            var from = points[i - 1];
            var to = points[i];
            var distance = (to - from).Length;
            var steps = Math.Max(1, (int)(distance / Math.Max(1, radius * 0.5)));
            for (var step = 1; step <= steps; step++)
                centers.Add(from + (to - from) * (step / (double)steps));
        }
        return centers;
    }

    private void EraseSegmentCore(WpfPoint fromFrame, WpfPoint toFrame, double radius) =>
        EraseCircles(BuildEraserCenters(new[] { fromFrame, toFrame }, radius), radius);

    private void EraseCircleCore(WpfPoint frameCenter, double radius) =>
        EraseCircles(new List<WpfPoint> { frameCenter }, radius);

    /// <summary>Clears circular brush stamps in one lock/dirty-rect batch — per-circle locking makes eraser drags stutter.</summary>
    private unsafe void EraseCircles(List<WpfPoint> centers, double radius)
    {
        if (centers.Count == 0) return;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var center in centers)
        {
            minX = Math.Min(minX, center.X - radius);
            minY = Math.Min(minY, center.Y - radius);
            maxX = Math.Max(maxX, center.X + radius);
            maxY = Math.Max(maxY, center.Y + radius);
        }
        var dirtyLeft = Math.Clamp((int)Math.Floor(minX), 0, _frame.Width);
        var dirtyTop = Math.Clamp((int)Math.Floor(minY), 0, _frame.Height);
        var dirtyRight = Math.Clamp((int)Math.Ceiling(maxX), dirtyLeft, _frame.Width);
        var dirtyBottom = Math.Clamp((int)Math.Ceiling(maxY), dirtyTop, _frame.Height);
        if (dirtyRight <= dirtyLeft || dirtyBottom <= dirtyTop) return;

        _annotationBitmap.Lock();
        var stride = _annotationBitmap.BackBufferStride;
        var buffer = (byte*)_annotationBitmap.BackBuffer;
        var radiusSquared = radius * radius;
        foreach (var center in centers)
        {
            var left = Math.Clamp((int)Math.Floor(center.X - radius), 0, _frame.Width);
            var top = Math.Clamp((int)Math.Floor(center.Y - radius), 0, _frame.Height);
            var right = Math.Clamp((int)Math.Ceiling(center.X + radius), left, _frame.Width);
            var bottom = Math.Clamp((int)Math.Ceiling(center.Y + radius), top, _frame.Height);
            for (var y = top; y < bottom; y++)
            {
                var row = buffer + y * stride;
                for (var x = left; x < right; x++)
                {
                    var dx = x + 0.5 - center.X;
                    var dy = y + 0.5 - center.Y;
                    if (dx * dx + dy * dy > radiusSquared) continue;
                    var d = x * 4;
                    row[d] = 0;
                    row[d + 1] = 0;
                    row[d + 2] = 0;
                    row[d + 3] = 0;
                }
            }
        }
        _annotationBitmap.AddDirtyRect(new Int32Rect(dirtyLeft, dirtyTop, dirtyRight - dirtyLeft, dirtyBottom - dirtyTop));
        _annotationBitmap.Unlock();
    }

    // ---------------------------------------------------------------- live drawing (overlay coords)

    private void UpdateLiveDrawing(WpfPoint position)
    {
        switch (_tool)
        {
            case Tool.Pen when _livePoints is not null:
            {
                _livePoints.Add(position);
                if (_liveElement is null)
                {
                    var style = _inkStyles[Tool.Pen];
                    var polyline = new WpfPolyline
                    {
                        Stroke = new SolidColorBrush(style.Color),
                        StrokeThickness = style.WidthDip,
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        IsHitTestVisible = false,
                    };
                    foreach (var previous in _livePoints)
                        polyline.Points.Add(previous);
                    _liveElement = polyline;
                    _canvas.Children.Add(polyline);
                }
                else
                {
                    ((WpfPolyline)_liveElement).Points.Add(position);
                }
                break;
            }
            case Tool.Arrow:
            {
                var style = _inkStyles[Tool.Arrow];
                ReplaceLive(BuildArrowElement(_mouseDownPoint, position, style.Color, style.WidthDip));
                break;
            }
            case Tool.Shape:
            {
                var style = _inkStyles[Tool.Shape];
                ReplaceLive(BuildShapeElement(NormalizeRect(_mouseDownPoint, position), style.Color, style.WidthDip, _shapeKind));
                break;
            }
            case Tool.Mosaic:
                ReplaceLive(BuildInvertedSelectionRect(NormalizeRect(_mouseDownPoint, position)));
                break;
        }
    }

    private void ReplaceLive(UIElement element)
    {
        if (_liveElement is not null) _canvas.Children.Remove(_liveElement);
        _liveElement = element;
        _canvas.Children.Add(element);
    }

    private void CommitLiveDrawing(WpfPoint position)
    {
        if (_liveElement is not null)
        {
            _canvas.Children.Remove(_liveElement);
            _liveElement = null;
        }

        var scale = OverlayToFrameScale();
        Annotation? annotation = _tool switch
        {
            Tool.Pen when _livePoints is { Count: > 1 } =>
                new StrokeAnnotation(_livePoints.Select(OverlayPointToFrame).ToList(), _inkStyles[Tool.Pen].Color, _inkStyles[Tool.Pen].WidthDip * scale),
            Tool.Arrow when Distance(_mouseDownPoint, position) > DragThreshold =>
                new ArrowAnnotation(OverlayPointToFrame(_mouseDownPoint), OverlayPointToFrame(position), _inkStyles[Tool.Arrow].Color, _inkStyles[Tool.Arrow].WidthDip * scale),
            Tool.Shape when Distance(_mouseDownPoint, position) > DragThreshold && _shapeKind == ShapeKind.Rect =>
                new RectAnnotation(FrameRectOf(_mouseDownPoint, position), _inkStyles[Tool.Shape].Color, _inkStyles[Tool.Shape].WidthDip * scale),
            Tool.Shape when Distance(_mouseDownPoint, position) > DragThreshold =>
                new EllipseAnnotation(FrameRectOf(_mouseDownPoint, position), _inkStyles[Tool.Shape].Color, _inkStyles[Tool.Shape].WidthDip * scale),
            Tool.Mosaic when Distance(_mouseDownPoint, position) > DragThreshold =>
                new MosaicAnnotation(OverlayToFrame(ClampToImage(NormalizeRect(_mouseDownPoint, position))), _mosaicBlockFramePx),
            _ => null,
        };
        _livePoints = null;
        if (annotation is not null)
            CommitOperation(annotation);
    }

    private Rect FrameRectOf(WpfPoint a, WpfPoint b)
    {
        var rect = OverlayToFrame(ClampToImage(NormalizeRect(a, b)));
        return new Rect(rect.X, rect.Y, rect.Width, rect.Height);
    }

    private static double Distance(WpfPoint a, WpfPoint b) => (a - b).Length;

    private static UIElement BuildArrowElement(WpfPoint from, WpfPoint to, WpfColor color, double width)
    {
        var (shaft, head) = ArrowGeometry.Build(from, to, width);
        var brush = new SolidColorBrush(color);
        var container = new Canvas { IsHitTestVisible = false };
        container.Children.Add(new WpfPath { Data = shaft, Stroke = brush, StrokeThickness = width, StrokeStartLineCap = PenLineCap.Round });
        if (head is not null)
            container.Children.Add(new WpfPath { Data = head, Fill = brush });
        return container;
    }

    private static UIElement BuildShapeElement(Rect bounds, WpfColor color, double width, ShapeKind kind)
    {
        WpfShape shape = kind == ShapeKind.Ellipse
            ? new WpfEllipse()
            : new WpfRectangle { RadiusX = 2, RadiusY = 2 };
        shape.Stroke = new SolidColorBrush(color);
        shape.StrokeThickness = width;
        shape.Width = Math.Max(1, bounds.Width);
        shape.Height = Math.Max(1, bounds.Height);
        shape.IsHitTestVisible = false;
        Canvas.SetLeft(shape, bounds.X);
        Canvas.SetTop(shape, bounds.Y);
        return shape;
    }

    /// <summary>High-contrast mosaic selection border: alternating black/white dashes stay visible on any content.</summary>
    private static UIElement BuildInvertedSelectionRect(Rect bounds)
    {
        var container = new Canvas { IsHitTestVisible = false };
        foreach (var (brush, offset) in new[] { (Brushes.White, 0.0), (Brushes.Black, 5.0) })
        {
            var rect = new WpfRectangle
            {
                Width = Math.Max(1, bounds.Width),
                Height = Math.Max(1, bounds.Height),
                Stroke = brush,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 5, 5 },
                StrokeDashOffset = offset,
            };
            Canvas.SetLeft(rect, bounds.X);
            Canvas.SetTop(rect, bounds.Y);
            container.Children.Add(rect);
        }
        return container;
    }

    private void UpdateEraserCursor(WpfPoint position)
    {
        var diameter = _eraserRadiusDip * 2;
        _eraserCursor.Width = diameter;
        _eraserCursor.Height = diameter;
        Canvas.SetLeft(_eraserCursor, position.X - _eraserRadiusDip);
        Canvas.SetTop(_eraserCursor, position.Y - _eraserRadiusDip);
        _eraserCursor.Visibility = Visibility.Visible;
    }

    // ---------------------------------------------------------------- magnifier / color picker

    private const string DefaultMagnifierHint = "C 复制颜色 · Shift 切换格式";

    private (Border Panel, TextBlock Pos, TextBlock Color, TextBlock Hint) BuildMagnifier(WriteableBitmap bitmap)
    {
        var image = new WpfImage
        {
            Source = bitmap,
            Width = MagnifierSourcePixels * MagnifierScale,
            Height = MagnifierSourcePixels * MagnifierScale,
            Stretch = Stretch.Fill,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);

        var accent = new SolidColorBrush(WpfColor.FromArgb(150, 40, 160, 255));
        var view = new Grid { Width = image.Width, Height = image.Height };
        view.Children.Add(image);
        view.Children.Add(new WpfRectangle { Width = 1, Height = image.Height, Fill = accent, HorizontalAlignment = HorizontalAlignment.Center });
        view.Children.Add(new WpfRectangle { Width = image.Width, Height = 1, Fill = accent, VerticalAlignment = VerticalAlignment.Center });
        view.Children.Add(new WpfRectangle
        {
            Width = MagnifierScale,
            Height = MagnifierScale,
            Stroke = Brushes.White,
            StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var pos = new TextBlock { Foreground = _chrome.Text, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
        var color = new TextBlock { Foreground = _chrome.Text, FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
        var hint = new TextBlock
        {
            Text = DefaultMagnifierHint,
            Foreground = _chrome.SubText,
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0),
        };

        var panel = new StackPanel();
        panel.Children.Add(view);
        panel.Children.Add(pos);
        panel.Children.Add(color);
        panel.Children.Add(hint);

        return (new Border
        {
            Background = _chrome.PanelBg,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            Child = panel,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        }, pos, color, hint);
    }

    private void UpdateMagnifier(WpfPoint position)
    {
        var framePoint = OverlayPointToFrame(position);
        var frameX = Math.Clamp((int)framePoint.X, 0, _frame.Width - 1);
        var frameY = Math.Clamp((int)framePoint.Y, 0, _frame.Height - 1);

        var half = MagnifierSourcePixels / 2;
        for (var y = 0; y < MagnifierSourcePixels; y++)
        {
            for (var x = 0; x < MagnifierSourcePixels; x++)
            {
                var sourceX = frameX - half + x;
                var sourceY = frameY - half + y;
                var d = (y * MagnifierSourcePixels + x) * 4;
                if (sourceX < 0 || sourceY < 0 || sourceX >= _frame.Width || sourceY >= _frame.Height)
                {
                    _magnifierPixels[d] = 0;
                    _magnifierPixels[d + 1] = 0;
                    _magnifierPixels[d + 2] = 0;
                    _magnifierPixels[d + 3] = 255;
                    continue;
                }
                var s = (sourceY * _frame.Width + sourceX) * 4;
                _magnifierPixels[d] = _previewBgra[s];
                _magnifierPixels[d + 1] = _previewBgra[s + 1];
                _magnifierPixels[d + 2] = _previewBgra[s + 2];
                _magnifierPixels[d + 3] = 255;
            }
        }
        _magnifierBitmap.WritePixels(new Int32Rect(0, 0, MagnifierSourcePixels, MagnifierSourcePixels),
            _magnifierPixels, MagnifierSourcePixels * 4, 0);

        var center = (frameY * _frame.Width + frameX) * 4;
        _pickedColor = (_previewBgra[center + 2], _previewBgra[center + 1], _previewBgra[center]);
        _magnifierPos.Text = $"POS ({frameX}, {frameY})";
        _magnifierColor.Text = FormatPickedColor();

        // Bottom-right of the cursor by default, flipping at the screen edges.
        _magnifier.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = _magnifier.DesiredSize.Width;
        var height = _magnifier.DesiredSize.Height;
        var left = position.X + 24;
        if (left + width > ActualWidth - 8) left = position.X - width - 24;
        var top = position.Y + 24;
        if (top + height > ActualHeight - 8) top = position.Y - height - 24;
        Canvas.SetLeft(_magnifier, Math.Max(8, left));
        Canvas.SetTop(_magnifier, Math.Max(8, top));
        _magnifier.Visibility = Visibility.Visible;
    }

    private string FormatPickedColor() => _colorAsHex
        ? $"HEX  #{_pickedColor.R:X2}{_pickedColor.G:X2}{_pickedColor.B:X2}"
        : $"RGB  ({_pickedColor.R}, {_pickedColor.G}, {_pickedColor.B})";

    private void CopyPickedColor()
    {
        var value = _colorAsHex
            ? $"#{_pickedColor.R:X2}{_pickedColor.G:X2}{_pickedColor.B:X2}"
            : $"rgb({_pickedColor.R}, {_pickedColor.G}, {_pickedColor.B})";
        try
        {
            Clipboard.SetText(value);
            _magnifierHint.Text = $"已复制 {value}";
            _hintReset.Stop();
            _hintReset.Start();
        }
        catch
        {
            // Clipboard transiently locked by another process; ignore.
        }
    }

    // ---------------------------------------------------------------- toolbar

    private Border BuildToolbar()
    {
        var toolsRow = new StackPanel { Orientation = Orientation.Horizontal };
        AddToolButton(toolsRow, Tool.Pen, "\uE70F", "涂鸦", mdl2: true);
        AddToolButton(toolsRow, Tool.Arrow, "↗", "箭头", mdl2: false);
        AddToolButton(toolsRow, Tool.Shape, "▭", "框选（矩形 / 椭圆）", mdl2: false);
        AddToolButton(toolsRow, Tool.Mosaic, "▦", "马赛克", mdl2: false);
        AddToolButton(toolsRow, Tool.Eraser, "\uE75C", "橡皮擦（按住拖动擦除）", mdl2: true);
        AddSeparator(toolsRow);
        AddActionButton(toolsRow, ScissorsIcon(), "长截图（自动滚动拼接）", StartScrollCapture);
        AddActionButton(toolsRow, OcrIcon(), "文字识别（OCR）", RunOcr);
        AddSeparator(toolsRow);
        AddActionButton(toolsRow, "\uE7A7", "撤销 (Ctrl+Z)", Undo, mdl2: true);
        AddActionButton(toolsRow, "\uE74E", "另存为… (Ctrl+S)", SaveAs, mdl2: true);
        AddActionButton(toolsRow, "\uE73E", "完成：复制到剪贴板 (Enter / 双击)", FinishToClipboard, mdl2: true, accent: true);
        AddActionButton(toolsRow, "\uE711", "取消 (Esc)", Cancel, mdl2: true);

        return new Border
        {
            Background = _chrome.PanelBg,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 4, 6, 4),
            Child = toolsRow,
            // The toolbar always shows the normal Windows cursor, whatever tool is active.
            Cursor = Cursors.Arrow,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.5 },
        };
    }

    private void AddToolButton(StackPanel panel, Tool tool, string glyph, string tooltip, bool mdl2)
    {
        var button = new ToggleButton
        {
            Content = Glyph(glyph, mdl2),
            ToolTip = tooltip,
            Width = 32,
            Height = 32,
            Margin = new Thickness(2, 0, 2, 0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Foreground = _chrome.Text,
            Focusable = false,
        };
        button.Checked += (_, _) => SelectTool(tool);
        button.Unchecked += (_, _) => { if (_tool == tool) SelectTool(Tool.None); };
        panel.Children.Add(button);
        _toolButtons.Add((tool, button));
    }

    private void AddActionButton(StackPanel panel, string glyph, string tooltip, Action action, bool mdl2, bool accent = false) =>
        AddActionButton(panel, (UIElement)Glyph(glyph, mdl2, accent ? new SolidColorBrush(WpfColor.FromRgb(0x4C, 0xC2, 0x66)) : _chrome.Text), tooltip, action);

    private void AddActionButton(StackPanel panel, UIElement icon, string tooltip, Action action)
    {
        var button = new Button
        {
            Content = icon,
            ToolTip = tooltip,
            Width = 32,
            Height = 32,
            Margin = new Thickness(2, 0, 2, 0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Focusable = false,
        };
        button.Click += (_, _) => action();
        panel.Children.Add(button);
    }

    /// <summary>Long-screenshot icon: scissors under a dashed cut line.</summary>
    private UIElement ScissorsIcon()
    {
        var root = new Grid { Width = 16, Height = 16 };
        root.Children.Add(new WpfPath
        {
            Data = Geometry.Parse("M1,2.2 H15"),
            Stroke = _chrome.Text,
            StrokeThickness = 1.3,
            StrokeDashArray = new DoubleCollection { 1.7, 1.9 },
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        });
        var scissors = new GeometryGroup();
        scissors.Children.Add(Geometry.Parse("M5.3,10.3 L10.9,4.2 M10.7,10.3 L5.1,4.2"));
        scissors.Children.Add(new EllipseGeometry(new WpfPoint(4.3, 12.1), 1.8, 1.8));
        scissors.Children.Add(new EllipseGeometry(new WpfPoint(11.7, 12.1), 1.8, 1.8));
        root.Children.Add(new WpfPath
        {
            Data = scissors,
            Stroke = _chrome.Text,
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        });
        return root;
    }

    /// <summary>OCR icon: the letter A inside scan-frame corner brackets.</summary>
    private UIElement OcrIcon() => new WpfPath
    {
        Width = 16,
        Height = 16,
        Data = Geometry.Parse(
            "M1,4.5 V1 H4.5 M11.5,1 H15 V4.5 M15,11.5 V15 H11.5 M4.5,15 H1 V11.5 " +
            "M8,4.4 L5.5,11.6 M8,4.4 L10.5,11.6 M6.3,9.4 H9.7"),
        Stroke = _chrome.Text,
        StrokeThickness = 1.5,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
    };

    private TextBlock Glyph(string text, bool mdl2, Brush? brush = null) => new()
    {
        Text = text,
        FontSize = mdl2 ? 15 : 16,
        Foreground = brush ?? _chrome.Text,
        FontFamily = mdl2 ? new FontFamily("Segoe MDL2 Assets") : new FontFamily("Segoe UI Symbol"),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private void AddSeparator(StackPanel panel) => panel.Children.Add(new WpfRectangle
    {
        Width = 1,
        Height = 20,
        Fill = _chrome.Separator,
        Margin = new Thickness(4, 6, 4, 6),
    });

    private void SelectTool(Tool tool)
    {
        _tool = tool;
        foreach (var (buttonTool, button) in _toolButtons)
        {
            button.IsChecked = buttonTool == tool && tool != Tool.None;
            button.Background = button.IsChecked == true
                ? _chrome.ActiveBg
                : Brushes.Transparent;
        }
        Cursor = tool switch
        {
            Tool.None => Cursors.Arrow,
            Tool.Eraser => Cursors.None,
            _ => Cursors.Cross,
        };
        if (tool != Tool.Eraser)
            _eraserCursor.Visibility = Visibility.Collapsed;
        RebuildOptionsRow();
        if (_mode == OverlayMode.Editing)
            PositionOptionsPopup();
    }

    // ---------------------------------------------------------------- sub-toolbar (per-tool options)

    private void RebuildOptionsRow()
    {
        _optionsRow.Children.Clear();
        switch (_tool)
        {
            case Tool.Pen or Tool.Arrow:
                AddInkOptions(_tool);
                break;
            case Tool.Shape:
                AddShapeKindOptions();
                AddSeparator(_optionsRow);
                AddInkOptions(Tool.Shape);
                break;
            case Tool.Mosaic:
                AddSizeOptions(MosaicBlocksFramePx.Select(static block => (double)block).ToArray(),
                    _mosaicBlockFramePx, value => _mosaicBlockFramePx = (int)value, "块大小", square: true);
                break;
            case Tool.Eraser:
                AddSizeOptions(EraserRadiiDip, _eraserRadiusDip, value => _eraserRadiusDip = value, "橡皮大小", square: false);
                break;
            default:
                _optionsPopup.Visibility = Visibility.Collapsed;
                return;
        }
        _optionsPopup.Visibility = Visibility.Visible;
    }

    private void AddShapeKindOptions()
    {
        foreach (var (kind, glyph, tooltip) in new[] { (ShapeKind.Rect, "▭", "矩形"), (ShapeKind.Ellipse, "◯", "椭圆") })
        {
            var isActive = _shapeKind == kind;
            var button = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(2, 0, 2, 0),
                Background = isActive ? _chrome.ActiveBg : Brushes.Transparent,
                ToolTip = tooltip,
                Child = Glyph(glyph, mdl2: false),
            };
            var chosenKind = kind;
            button.MouseLeftButtonDown += (_, e) =>
            {
                _shapeKind = chosenKind;
                RebuildOptionsRow();
                e.Handled = true;
            };
            _optionsRow.Children.Add(button);
        }
    }

    private void AddInkOptions(Tool tool)
    {
        var style = _inkStyles[tool];
        foreach (var color in Palette)
        {
            var swatch = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(3, 5, 3, 5),
                Background = new SolidColorBrush(color),
                BorderThickness = new Thickness(2),
                BorderBrush = color == style.Color ? _chrome.Text : _chrome.SwatchRing,
            };
            var chosenColor = color;
            swatch.MouseLeftButtonDown += (_, e) =>
            {
                _inkStyles[tool] = _inkStyles[tool] with { Color = chosenColor };
                RebuildOptionsRow();
                e.Handled = true;
            };
            _optionsRow.Children.Add(swatch);
        }
        AddSeparator(_optionsRow);
        foreach (var width in InkWidthsDip)
        {
            var isActive = Math.Abs(style.WidthDip - width) < 0.01;
            var dot = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(1, 0, 1, 0),
                Background = isActive ? _chrome.ActiveBg : Brushes.Transparent,
                ToolTip = $"粗细 {width:0.#}",
                Child = new WpfEllipse
                {
                    Width = 4 + width * 1.6,
                    Height = 4 + width * 1.6,
                    Fill = _chrome.Text,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            var chosenWidth = width;
            dot.MouseLeftButtonDown += (_, e) =>
            {
                _inkStyles[tool] = _inkStyles[tool] with { WidthDip = chosenWidth };
                RebuildOptionsRow();
                e.Handled = true;
            };
            _optionsRow.Children.Add(dot);
        }
    }

    private void AddSizeOptions(double[] values, double current, Action<double> apply, string label, bool square)
    {
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            var isActive = Math.Abs(current - value) < 0.01;
            var visualSize = 6 + i * 5;
            FrameworkElement marker = square
                ? new WpfRectangle
                {
                    Width = visualSize,
                    Height = visualSize,
                    Fill = _chrome.Text,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                }
                : new WpfEllipse
                {
                    Width = visualSize,
                    Height = visualSize,
                    Stroke = _chrome.Text,
                    StrokeThickness = 1.5,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            var button = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(2, 0, 2, 0),
                Background = isActive ? _chrome.ActiveBg : Brushes.Transparent,
                ToolTip = $"{label} {value:0.#}",
                Child = marker,
            };
            var chosenValue = value;
            button.MouseLeftButtonDown += (_, e) =>
            {
                apply(chosenValue);
                RebuildOptionsRow();
                e.Handled = true;
            };
            _optionsRow.Children.Add(button);
        }
    }

    // ---------------------------------------------------------------- shared visuals

    private void RefreshVisuals()
    {
        if (_targetFrameRect is { } target && _imageBounds.Width > 0 && _imageBounds.Height > 0)
        {
            var rect = FrameToOverlay(target);
            Canvas.SetLeft(_border, rect.Left);
            Canvas.SetTop(_border, rect.Top);
            _border.Width = rect.Width;
            _border.Height = rect.Height;
            _border.Visibility = Visibility.Visible;

            _sizeText.Text = $"{target.Width} × {target.Height}";
            _sizeLabel.Visibility = Visibility.Visible;
            _sizeLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var labelLeft = Math.Clamp(rect.Left, 0, Math.Max(0, ActualWidth - _sizeLabel.DesiredSize.Width));
            var labelTop = rect.Top - _sizeLabel.DesiredSize.Height - 4;
            if (labelTop < 0) labelTop = rect.Top + 4;
            Canvas.SetLeft(_sizeLabel, labelLeft);
            Canvas.SetTop(_sizeLabel, labelTop);
            UpdateHandles(rect);
        }
        else
        {
            _border.Visibility = Visibility.Collapsed;
            _sizeLabel.Visibility = Visibility.Collapsed;
            foreach (var handle in _handles)
                handle.Visibility = Visibility.Collapsed;
        }
        UpdateDim();
    }

    private void UpdateHandles(Rect rect)
    {
        if (_mode != OverlayMode.Editing)
        {
            foreach (var handle in _handles)
                handle.Visibility = Visibility.Collapsed;
            return;
        }
        var centerX = (rect.Left + rect.Right) / 2;
        var centerY = (rect.Top + rect.Bottom) / 2;
        Span<(double X, double Y)> points = stackalloc (double, double)[]
        {
            (rect.Left, rect.Top), (centerX, rect.Top), (rect.Right, rect.Top),
            (rect.Left, centerY), (rect.Right, centerY),
            (rect.Left, rect.Bottom), (centerX, rect.Bottom), (rect.Right, rect.Bottom),
        };
        for (var i = 0; i < _handles.Count; i++)
        {
            Canvas.SetLeft(_handles[i], points[i].X - 4);
            Canvas.SetTop(_handles[i], points[i].Y - 4);
            _handles[i].Visibility = Visibility.Visible;
        }
    }

    private void UpdateDim()
    {
        if (_backdrop is not null)
        {
            _backdrop.PresentDim(_targetFrameRect);
            return;
        }
        _dimFullGeometry.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
        _dimHoleGeometry.Rect = _targetFrameRect is { } target && _imageBounds.Width > 0 && _imageBounds.Height > 0
            ? FrameToOverlay(target)
            : Rect.Empty;
    }

    private void UpdateImageBounds() => _imageBounds = new Rect(0, 0, ActualWidth, ActualHeight);

    private Int32Rect? HitTestWindow(WpfPoint framePoint)
    {
        foreach (var window in _windows)
        {
            var rect = window.FrameRect;
            if (framePoint.X >= rect.X && framePoint.X < rect.X + rect.Width &&
                framePoint.Y >= rect.Y && framePoint.Y < rect.Y + rect.Height)
                return rect;
        }
        return null;
    }

    // ---------------------------------------------------------------- coordinate mapping

    private double OverlayToFrameScale() => _imageBounds.Width > 0 ? _frame.Width / _imageBounds.Width : 1;

    private WpfPoint OverlayPointToFrame(WpfPoint point)
    {
        if (_imageBounds.Width <= 0 || _imageBounds.Height <= 0) return new WpfPoint(0, 0);
        return new WpfPoint(
            (point.X - _imageBounds.Left) / _imageBounds.Width * _frame.Width,
            (point.Y - _imageBounds.Top) / _imageBounds.Height * _frame.Height);
    }

    private Int32Rect OverlayToFrame(Rect rect)
    {
        var left = (int)Math.Floor((rect.Left - _imageBounds.Left) / _imageBounds.Width * _frame.Width);
        var top = (int)Math.Floor((rect.Top - _imageBounds.Top) / _imageBounds.Height * _frame.Height);
        var right = (int)Math.Ceiling((rect.Right - _imageBounds.Left) / _imageBounds.Width * _frame.Width);
        var bottom = (int)Math.Ceiling((rect.Bottom - _imageBounds.Top) / _imageBounds.Height * _frame.Height);
        left = Math.Clamp(left, 0, _frame.Width);
        top = Math.Clamp(top, 0, _frame.Height);
        right = Math.Clamp(right, left, _frame.Width);
        bottom = Math.Clamp(bottom, top, _frame.Height);
        return new Int32Rect(left, top, right - left, bottom - top);
    }

    private Rect FrameToOverlay(Int32Rect rect) => new(
        _imageBounds.Left + (double)rect.X / _frame.Width * _imageBounds.Width,
        _imageBounds.Top + (double)rect.Y / _frame.Height * _imageBounds.Height,
        (double)rect.Width / _frame.Width * _imageBounds.Width,
        (double)rect.Height / _frame.Height * _imageBounds.Height);

    private Rect ClampToImage(Rect rect)
    {
        var left = Math.Clamp(rect.Left, _imageBounds.Left, _imageBounds.Right);
        var top = Math.Clamp(rect.Top, _imageBounds.Top, _imageBounds.Bottom);
        var right = Math.Clamp(rect.Right, _imageBounds.Left, _imageBounds.Right);
        var bottom = Math.Clamp(rect.Bottom, _imageBounds.Top, _imageBounds.Bottom);
        return new Rect(new WpfPoint(Math.Min(left, right), Math.Min(top, bottom)), new WpfPoint(Math.Max(left, right), Math.Max(top, bottom)));
    }

    private WpfPoint ClampToSelection(WpfPoint point)
    {
        var rect = FrameToOverlay(_selection);
        return new WpfPoint(Math.Clamp(point.X, rect.Left, rect.Right), Math.Clamp(point.Y, rect.Top, rect.Bottom));
    }

    private static Rect NormalizeRect(WpfPoint first, WpfPoint second) => new(
        new WpfPoint(Math.Min(first.X, second.X), Math.Min(first.Y, second.Y)),
        new WpfPoint(Math.Max(first.X, second.X), Math.Max(first.Y, second.Y)));

    // ---------------------------------------------------------------- preview

    private static byte[] CreatePreviewPixels(HdrFrame frame, float sdrWhiteScale)
    {
        // SDR-referenced preview: divide out the monitor's SDR white scale so SDR content
        // matches the live desktop exactly while HDR highlights clip. Display-only buffer;
        // the exporter always receives the original HDR frame.
        //
        // A 64K-entry LUT maps every Half bit pattern straight to its sanitized, scaled,
        // clipped, sRGB-encoded byte; with row parallelism this takes ~10 ms at 4K, versus
        // hundreds of ms for the naive per-pixel Pow loop — the bulk of hotkey latency.
        var invScale = 1f / sdrWhiteScale;
        var lut = new byte[65536];
        for (var i = 0; i < lut.Length; i++)
            lut[i] = ToSrgb((float)BitConverter.UInt16BitsToHalf((ushort)i) * invScale);

        var width = frame.Width;
        var pixels = new byte[frame.Width * frame.Height * 4];
        System.Threading.Tasks.Parallel.For(0, frame.Height, y =>
        {
            var bits = System.Runtime.InteropServices.MemoryMarshal.Cast<Half, ushort>(
                frame.Pixels.AsSpan(y * width * 4, width * 4));
            var row = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var s = x * 4;
                pixels[row + s] = lut[bits[s + 2]];
                pixels[row + s + 1] = lut[bits[s + 1]];
                pixels[row + s + 2] = lut[bits[s]];
                pixels[row + s + 3] = 255;
            }
        });
        return pixels;
    }

    private static byte ToSrgb(float linear)
    {
        linear = float.IsFinite(linear) ? Math.Clamp(linear, 0f, 1f) : 0f;
        var srgb = linear <= 0.0031308f ? linear * 12.92f : 1.055f * MathF.Pow(linear, 1 / 2.4f) - 0.055f;
        return (byte)Math.Clamp(MathF.Round(srgb * 255), 0, 255);
    }

    /// <summary>Premultiplied sRGB ink byte -> premultiplied linear scRGB at the SDR white level.</summary>
    private float InkToLinear(byte premultiplied, float alpha)
    {
        if (alpha <= 0) return 0f;
        var encoded = Math.Clamp(premultiplied / 255f / alpha, 0f, 1f);
        var linear = encoded <= 0.04045f ? encoded / 12.92f : MathF.Pow((encoded + 0.055f) / 1.055f, 2.4f);
        return linear * _sdrWhiteScale * alpha;
    }
}
