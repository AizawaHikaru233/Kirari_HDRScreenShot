using System.Runtime.InteropServices;
using RapidOcrNet;
using SkiaSharp;

namespace HdrCapture;

/// <summary>
/// Text recognition over a BGRA buffer using RapidOCR (PP-OCR ONNX models via onnxruntime) —
/// a fully open-source stack, offline, with strong Chinese/English accuracy. Tall images
/// (long screenshots) are tiled so the detector's internal downscaling does not destroy small
/// text; fragment blocks are recomposed into visual lines.
/// </summary>
internal static class OcrService
{
    private const int TileHeight = 1600;
    private const int TileOverlap = 80;

    private static RapidOcr? _engine;
    private static readonly object EngineLock = new();
    private static Timer? _idleTimer;
    // The engine holds substantial native inference memory; release it after idling.
    private static readonly TimeSpan EngineIdleLifetime = TimeSpan.FromSeconds(90);

    public static Task<string> RecognizeAsync(byte[] bgra, int width, int height) =>
        Task.Run(() =>
        {
            lock (EngineLock)
            {
                _idleTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                try
                {
                    return Recognize(bgra, width, height);
                }
                finally
                {
                    _idleTimer ??= new Timer(static _ => ReleaseEngine(), null, Timeout.Infinite, Timeout.Infinite);
                    _idleTimer.Change(EngineIdleLifetime, Timeout.InfiniteTimeSpan);
                }
            }
        });

    private static void ReleaseEngine()
    {
        lock (EngineLock)
        {
            _engine?.Dispose();
            _engine = null;
        }
        MemoryTrimmer.Trim();
    }

    private sealed record OcrBlock(float Left, float Top, float Right, float Bottom, string Text);

    private static RapidOcr GetEngine()
    {
        if (_engine is null)
        {
            // Single-file apps use AppContext.BaseDirectory as their extraction cache. OCR
            // models deliberately stay beside the user-visible executable, so resolve them
            // from the process image instead. This is the same directory for normal builds.
            var executablePath = Environment.ProcessPath;
            var applicationDirectory = !string.IsNullOrWhiteSpace(executablePath)
                ? System.IO.Path.GetDirectoryName(executablePath)!
                : AppContext.BaseDirectory;
            var models = System.IO.Path.Combine(applicationDirectory, "models", "v5");
            _engine = new RapidOcr();
            _engine.InitModels(
                detPath: System.IO.Path.Combine(models, "ch_PP-OCRv5_mobile_det.onnx"),
                clsPath: System.IO.Path.Combine(models, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
                recPath: System.IO.Path.Combine(models, "ch_PP-OCRv5_rec_mobile.onnx"),
                keysPath: System.IO.Path.Combine(models, "ppocrv5_dict.txt"));
        }
        return _engine;
    }

    private static string Recognize(byte[] bgra, int width, int height)
    {
        var engine = GetEngine();
        var blocks = new List<OcrBlock>();
        var top = 0;
        while (top < height)
        {
            var tileHeight = Math.Min(TileHeight, height - top);
            var isLast = top + tileHeight >= height;
            using var bitmap = CreateBitmap(bgra, width, height, top, tileHeight);
            var result = engine.Detect(bitmap, RapidOcrOptions.Default);
            if (result?.TextBlocks is not null)
            {
                foreach (var block in result.TextBlocks)
                {
                    var text = block.Text?.Trim();
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    var bounds = BlockBounds(block);
                    // Single-ownership boundary at the overlap midpoint: each tile keeps the
                    // half of the overlap it sees best, so nothing duplicates or drops.
                    if (top > 0 && bounds.Top < TileOverlap / 2.0) continue;
                    if (!isLast && bounds.Top >= tileHeight - TileOverlap / 2.0) continue;
                    blocks.Add(bounds with { Top = bounds.Top + top, Bottom = bounds.Bottom + top, Text = text });
                }
            }
            if (isLast) break;
            top += TileHeight - TileOverlap;
        }
        return ComposeText(blocks);
    }

    /// <summary>
    /// The detector emits fragment-level blocks: one visual text line is often split into
    /// several boxes (column gaps, wide spacing), which must not become separate output lines.
    /// Blocks are clustered into visual lines by vertical overlap, ordered left-to-right within
    /// a line, and joined without a space between adjacent CJK fragments (unless a wide column
    /// gap separates them).
    /// </summary>
    private static string ComposeText(List<OcrBlock> blocks)
    {
        var lines = new List<List<OcrBlock>>();
        foreach (var block in blocks.OrderBy(static b => (b.Top + b.Bottom) / 2))
        {
            List<OcrBlock>? home = null;
            foreach (var line in lines)
            {
                var lineTop = line.Min(static b => b.Top);
                var lineBottom = line.Max(static b => b.Bottom);
                var overlap = Math.Min(lineBottom, block.Bottom) - Math.Max(lineTop, block.Top);
                var minHeight = Math.Min(lineBottom - lineTop, block.Bottom - block.Top);
                if (minHeight > 0 && overlap > minHeight * 0.5)
                {
                    home = line;
                    break;
                }
            }
            if (home is null) lines.Add(new List<OcrBlock> { block });
            else home.Add(block);
        }

        var builder = new System.Text.StringBuilder();
        foreach (var line in lines.OrderBy(static l => l.Average(static b => (b.Top + b.Bottom) / 2)))
        {
            var parts = line.OrderBy(static b => b.Left).ToList();
            var lineHeight = line.Average(static b => b.Bottom - b.Top);
            for (var i = 0; i < parts.Count; i++)
            {
                if (i > 0)
                {
                    var gap = parts[i].Left - parts[i - 1].Right;
                    var cjkAdjacent = IsCjk(LastChar(parts[i - 1].Text)) && IsCjk(FirstChar(parts[i].Text));
                    if (!cjkAdjacent || gap > lineHeight * 0.8)
                        builder.Append(' ');
                }
                builder.Append(parts[i].Text);
            }
            builder.Append(Environment.NewLine);
        }
        return builder.ToString().TrimEnd();
    }

    private static char FirstChar(string text) => text.Length > 0 ? text[0] : ' ';
    private static char LastChar(string text) => text.Length > 0 ? text[^1] : ' ';

    private static bool IsCjk(char c) =>
        (c >= 0x2E80 && c <= 0x9FFF) || (c >= 0xF900 && c <= 0xFAFF) ||
        (c >= 0x3000 && c <= 0x303F) || (c >= 0xFF00 && c <= 0xFFEF);

    private static OcrBlock BlockBounds(TextBlock block)
    {
        float left = float.MaxValue, top = float.MaxValue, right = float.MinValue, bottom = float.MinValue;
        foreach (var point in block.BoxPoints)
        {
            left = Math.Min(left, point.X);
            top = Math.Min(top, point.Y);
            right = Math.Max(right, point.X);
            bottom = Math.Max(bottom, point.Y);
        }
        if (left == float.MaxValue) return new OcrBlock(0, 0, 0, 0, string.Empty);
        return new OcrBlock(left, top, right, bottom, string.Empty);
    }

    private static SKBitmap CreateBitmap(byte[] bgra, int width, int height, int top, int rows)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, rows, SKColorType.Bgra8888, SKAlphaType.Opaque));
        Marshal.Copy(bgra, top * width * 4, bitmap.GetPixels(), width * rows * 4);
        return bitmap;
    }
}
