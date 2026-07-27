using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;

namespace HdrCapture;

/// <summary>
/// Annotation operations created in the capture overlay, in chronological order. All
/// coordinates, widths and radii are in capture-frame pixels so they stay resolution-exact
/// regardless of overlay DPI scaling. The eraser is itself an operation: it erases whatever
/// annotation content exists at that point in the timeline, so ink drawn later is unaffected.
/// </summary>
internal abstract record Annotation;

internal sealed record StrokeAnnotation(IReadOnlyList<WpfPoint> Points, WpfColor Color, double Width) : Annotation;
internal sealed record ArrowAnnotation(WpfPoint From, WpfPoint To, WpfColor Color, double Width) : Annotation;
internal sealed record RectAnnotation(Rect Bounds, WpfColor Color, double Width) : Annotation;
internal sealed record EllipseAnnotation(Rect Bounds, WpfColor Color, double Width) : Annotation;
internal sealed record MosaicAnnotation(Int32Rect Bounds, int BlockSize) : Annotation;
internal sealed record EraserAnnotation(IReadOnlyList<WpfPoint> Points, double Radius) : Annotation;

/// <summary>
/// Crops the HDR frame and bakes the annotation timeline into it. The annotation layer is a
/// premultiplied linear-scRGB float buffer composited operation by operation: ink rasterizes
/// via WPF at the SDR white level, mosaic writes linear HDR block averages, and the eraser
/// attenuates layer alpha. The final layer is source-over composited onto the HDR crop, so the
/// export keeps full HDR precision and annotations read correctly in both HDR and SDR.
/// </summary>
internal static class AnnotationBaker
{
    public static HdrFrame Bake(HdrFrame frame, Int32Rect region, IReadOnlyList<Annotation> operations, float sdrWhiteScale)
    {
        var crop = Crop(frame, region);
        if (operations.Count == 0) return crop;

        var width = crop.Width;
        var height = crop.Height;
        var layer = new float[width * height * 4]; // premultiplied linear scRGB, RGBA

        var index = 0;
        while (index < operations.Count)
        {
            switch (operations[index])
            {
                case MosaicAnnotation mosaic:
                    ApplyMosaic(layer, crop, Translate(mosaic.Bounds, region), mosaic.BlockSize);
                    index++;
                    break;
                case EraserAnnotation eraser:
                    ApplyEraser(layer, eraser, region, width, height);
                    index++;
                    break;
                default:
                {
                    var batch = new List<Annotation>();
                    while (index < operations.Count && operations[index] is not MosaicAnnotation and not EraserAnnotation)
                        batch.Add(operations[index++]);
                    CompositeInk(layer, batch, region, width, height, sdrWhiteScale);
                    break;
                }
            }
        }

        for (var pixel = 0; pixel < width * height; pixel++)
        {
            var alpha = layer[pixel * 4 + 3];
            if (alpha <= 0) continue;
            var p = pixel * 4;
            crop.Pixels[p] = (Half)((float)crop.Pixels[p] * (1 - alpha) + layer[p]);
            crop.Pixels[p + 1] = (Half)((float)crop.Pixels[p + 1] * (1 - alpha) + layer[p + 1]);
            crop.Pixels[p + 2] = (Half)((float)crop.Pixels[p + 2] * (1 - alpha) + layer[p + 2]);
        }
        return crop;
    }

    public static HdrFrame Crop(HdrFrame source, Int32Rect rect)
    {
        var pixels = new Half[rect.Width * rect.Height * 4];
        for (var y = 0; y < rect.Height; y++)
            Array.Copy(source.Pixels, ((rect.Y + y) * source.Width + rect.X) * 4, pixels, y * rect.Width * 4, rect.Width * 4);
        return new HdrFrame { Width = rect.Width, Height = rect.Height, Pixels = pixels, Display = source.Display };
    }

    /// <summary>Draws one ink annotation (stroke/arrow/rect/ellipse) into a drawing context.</summary>
    public static void DrawInk(DrawingContext context, Annotation annotation)
    {
        switch (annotation)
        {
            case StrokeAnnotation stroke when stroke.Points.Count > 1:
            {
                var geometry = new StreamGeometry();
                using (var figure = geometry.Open())
                {
                    figure.BeginFigure(stroke.Points[0], false, false);
                    figure.PolyLineTo(stroke.Points.Skip(1).ToList(), true, true);
                }
                context.DrawGeometry(null, CreatePen(stroke.Color, stroke.Width), geometry);
                break;
            }
            case ArrowAnnotation arrow:
            {
                var (shaft, head) = ArrowGeometry.Build(arrow.From, arrow.To, arrow.Width);
                context.DrawGeometry(null, CreatePen(arrow.Color, arrow.Width), shaft);
                if (head is not null)
                    context.DrawGeometry(new SolidColorBrush(arrow.Color), null, head);
                break;
            }
            case RectAnnotation rect:
                context.DrawRectangle(null, CreatePen(rect.Color, rect.Width), rect.Bounds);
                break;
            case EllipseAnnotation ellipse:
                context.DrawEllipse(null, CreatePen(ellipse.Color, ellipse.Width),
                    new WpfPoint(ellipse.Bounds.X + ellipse.Bounds.Width / 2, ellipse.Bounds.Y + ellipse.Bounds.Height / 2),
                    ellipse.Bounds.Width / 2, ellipse.Bounds.Height / 2);
                break;
        }
    }

    private static Int32Rect Translate(Int32Rect rect, Int32Rect region) =>
        new(rect.X - region.X, rect.Y - region.Y, rect.Width, rect.Height);

    private static void CompositeInk(float[] layer, List<Annotation> batch, Int32Rect region, int width, int height, float sdrWhiteScale)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.PushTransform(new TranslateTransform(-region.X, -region.Y));
            foreach (var annotation in batch)
                DrawInk(context, annotation);
            context.Pop();
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var ink = new byte[width * height * 4];
        bitmap.CopyPixels(ink, width * 4, 0);

        for (var pixel = 0; pixel < width * height; pixel++)
        {
            var alpha8 = ink[pixel * 4 + 3];
            if (alpha8 == 0) continue;
            var alpha = alpha8 / 255f;
            // Pbgra32 is premultiplied; un-premultiply, linearize, scale to SDR white, re-premultiply.
            var blue = SrgbToLinear(ink[pixel * 4] / 255f / alpha) * sdrWhiteScale * alpha;
            var green = SrgbToLinear(ink[pixel * 4 + 1] / 255f / alpha) * sdrWhiteScale * alpha;
            var red = SrgbToLinear(ink[pixel * 4 + 2] / 255f / alpha) * sdrWhiteScale * alpha;
            var p = pixel * 4;
            layer[p] = red + layer[p] * (1 - alpha);
            layer[p + 1] = green + layer[p + 1] * (1 - alpha);
            layer[p + 2] = blue + layer[p + 2] * (1 - alpha);
            layer[p + 3] = alpha + layer[p + 3] * (1 - alpha);
        }
    }

    private static void ApplyMosaic(float[] layer, HdrFrame crop, Int32Rect bounds, int blockSize)
    {
        var left = Math.Clamp(bounds.X, 0, crop.Width);
        var top = Math.Clamp(bounds.Y, 0, crop.Height);
        var right = Math.Clamp(bounds.X + bounds.Width, left, crop.Width);
        var bottom = Math.Clamp(bounds.Y + bounds.Height, top, crop.Height);

        for (var blockY = top; blockY < bottom; blockY += blockSize)
        {
            for (var blockX = left; blockX < right; blockX += blockSize)
            {
                var endX = Math.Min(blockX + blockSize, right);
                var endY = Math.Min(blockY + blockSize, bottom);
                double sumR = 0, sumG = 0, sumB = 0;
                var count = (endX - blockX) * (endY - blockY);
                for (var y = blockY; y < endY; y++)
                {
                    for (var x = blockX; x < endX; x++)
                    {
                        // Average the CURRENT composite (frame + annotations so far), so the
                        // mosaic pixelates ink drawn before it; later ink covers the mosaic.
                        var p = (y * crop.Width + x) * 4;
                        var alpha = layer[p + 3];
                        sumR += (float)crop.Pixels[p] * (1 - alpha) + layer[p];
                        sumG += (float)crop.Pixels[p + 1] * (1 - alpha) + layer[p + 1];
                        sumB += (float)crop.Pixels[p + 2] * (1 - alpha) + layer[p + 2];
                    }
                }
                var avgR = (float)(sumR / count);
                var avgG = (float)(sumG / count);
                var avgB = (float)(sumB / count);
                for (var y = blockY; y < endY; y++)
                {
                    for (var x = blockX; x < endX; x++)
                    {
                        var p = (y * crop.Width + x) * 4;
                        layer[p] = avgR;
                        layer[p + 1] = avgG;
                        layer[p + 2] = avgB;
                        layer[p + 3] = 1f;
                    }
                }
            }
        }
    }

    private static void ApplyEraser(float[] layer, EraserAnnotation eraser, Int32Rect region, int width, int height)
    {
        if (eraser.Points.Count == 0) return;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.PushTransform(new TranslateTransform(-region.X, -region.Y));
            var pen = new Pen(Brushes.White, eraser.Radius * 2)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            if (eraser.Points.Count == 1)
            {
                context.DrawEllipse(Brushes.White, null, eraser.Points[0], eraser.Radius, eraser.Radius);
            }
            else
            {
                var geometry = new StreamGeometry();
                using (var figure = geometry.Open())
                {
                    figure.BeginFigure(eraser.Points[0], false, false);
                    figure.PolyLineTo(eraser.Points.Skip(1).ToList(), true, true);
                }
                context.DrawGeometry(null, pen, geometry);
            }
            context.Pop();
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var mask = new byte[width * height * 4];
        bitmap.CopyPixels(mask, width * 4, 0);

        for (var pixel = 0; pixel < width * height; pixel++)
        {
            var coverage = mask[pixel * 4 + 3] / 255f;
            if (coverage <= 0) continue;
            var keep = 1 - coverage;
            var p = pixel * 4;
            layer[p] *= keep;
            layer[p + 1] *= keep;
            layer[p + 2] *= keep;
            layer[p + 3] *= keep;
        }
    }

    private static Pen CreatePen(WpfColor color, double width) => new(new SolidColorBrush(color), width)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    };

    private static float SrgbToLinear(float encoded)
    {
        encoded = Math.Clamp(encoded, 0f, 1f);
        return encoded <= 0.04045f ? encoded / 12.92f : MathF.Pow((encoded + 0.055f) / 1.055f, 2.4f);
    }
}

/// <summary>Shared arrow shape math for the overlay preview and the baker.</summary>
internal static class ArrowGeometry
{
    /// <summary>Returns the shaft line geometry and the filled head triangle (null when degenerate).</summary>
    public static (Geometry Shaft, Geometry? Head) Build(WpfPoint from, WpfPoint to, double width)
    {
        var direction = to - from;
        var length = direction.Length;
        if (length < 0.01)
            return (new LineGeometry(from, to), null);

        direction /= length;
        // Head length wants to be at least 10 but can never exceed the arrow itself; the
        // ordering matters because the arrow is shorter than 10 while a drag starts.
        var headLength = Math.Min(Math.Max(width * 4, 10), length);
        var headWidth = headLength * 0.7;
        var basePoint = to - direction * headLength;
        var normal = new Vector(-direction.Y, direction.X);

        var head = new StreamGeometry();
        using (var figure = head.Open())
        {
            figure.BeginFigure(to, true, true);
            figure.LineTo(basePoint + normal * (headWidth / 2), true, true);
            figure.LineTo(basePoint - normal * (headWidth / 2), true, true);
        }
        return (new LineGeometry(from, basePoint), head);
    }
}
