namespace HdrCapture;

public partial class App : System.Windows.Application
{
    private MainWindow? _backgroundHost;
    private System.Threading.Mutex? _instanceMutex;
    private System.Threading.EventWaitHandle? _settingsSignal;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        if (e.Args.Contains("--verify-container", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                ContainerSelfTest.Run();
                Shutdown(0);
            }
            catch
            {
                Shutdown(1);
            }
            return;
        }

        var ocr = Array.FindIndex(e.Args, argument => argument.Equals("--ocr", StringComparison.OrdinalIgnoreCase));
        if (ocr >= 0)
        {
            try
            {
                if (ocr + 1 >= e.Args.Length) throw new ArgumentException("--ocr requires an image path.");
                var path = e.Args[ocr + 1];
                using var bitmap = new System.Drawing.Bitmap(path);
                var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
                    System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var bgra = new byte[bitmap.Width * bitmap.Height * 4];
                try
                {
                    for (var y = 0; y < bitmap.Height; y++)
                        System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, bgra, y * bitmap.Width * 4, bitmap.Width * 4);
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }
                var text = OcrService.RecognizeAsync(bgra, bitmap.Width, bitmap.Height).GetAwaiter().GetResult();
                System.IO.File.WriteAllText(path + ".txt", text);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                try { System.IO.File.WriteAllText(e.Args[ocr + 1] + ".err.txt", ex.ToString()); } catch { }
                Shutdown(1);
            }
            return;
        }

        var resign = Array.FindIndex(e.Args, argument => argument.Equals("--resign", StringComparison.OrdinalIgnoreCase));
        if (resign >= 0)
        {
            try
            {
                if (resign + 1 >= e.Args.Length) throw new ArgumentException("--resign requires a PNG path.");
                PngResigner.Resign(e.Args[resign + 1]);
                Shutdown(0);
            }
            catch
            {
                Shutdown(1);
            }
            return;
        }
        // Single instance: a second launch signals the first one to open settings and exits.
        // This is the escape hatch when the tray icon is hidden.
        _settingsSignal = new System.Threading.EventWaitHandle(false,
            System.Threading.EventResetMode.AutoReset, "Kirari.OpenSettings");
        _instanceMutex = new System.Threading.Mutex(true, "Kirari.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            _settingsSignal.Set();
            Shutdown(0);
            return;
        }
        var listener = new System.Threading.Thread(() =>
        {
            while (_settingsSignal.WaitOne())
                Dispatcher.BeginInvoke(() => _backgroundHost?.ShowSettings());
        })
        { IsBackground = true };
        listener.Start();

        base.OnStartup(e);
        _backgroundHost = new MainWindow();
        MainWindow = _backgroundHost;
        _backgroundHost.StartBackground();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _backgroundHost?.Dispose();
        base.OnExit(e);
    }
}
