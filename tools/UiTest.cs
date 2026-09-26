using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Seek;

// Drives SearchWindow off-screen (no focus stealing) and renders each state to a PNG.
//   UiTest.exe <index.bin> <output folder>
static class UiTest
{
    static SearchWindow window;
    static TextBox box;
    static string outDir;

    [STAThread]
    static void Main(string[] args)
    {
        outDir = args[1];
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var index = new FileIndex(null);
        index.UseSnapshot(Snapshot.Open(args[0]));
        window = new SearchWindow(index);
        box = (TextBox)typeof(SearchWindow).GetField("box", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window);

        app.Dispatcher.BeginInvoke(new Action(() =>
        {
            Step("wild", "untitled*.pdf", Key.Down, Key.Down);
            Step("none", "zzqqxx");
            Step("exact", "notepad.exe");
            Step("folderish", "Downloads", Key.Down, Key.Down, Key.Down, Key.Down, Key.Down, Key.Down, Key.Up);
            Step("empty", "");
            Step("dot", ".");
            // Stepping into a folder and back out (null text = keep what's typed).
            Step("scope-1-found", "touhou", Key.Down);
            Step("scope-2-inside", null, Key.Tab);
            Step("scope-3-typed", ".exe");
            Step("scope-4-back", "", Key.Back);
            Step("scope-5-right-left", null, Key.Right, Key.Left);
            Step("scope-6-deeper", "Downloads", Key.Tab, Key.Tab); // 2nd Tab: a file is selected, so nothing
            Step("scope-7-esc-out", null, Key.Escape);
            // Mouse: click the › on the first folder row to go in, then the chip to come back.
            var rows = (System.Collections.Generic.List<Border>)typeof(SearchWindow).GetField("rows", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window);
            var firstContent = (Grid)((Grid)rows[0].Child).Children[1];
            Click((UIElement)firstContent.Children[3]);
            Step("scope-8-chevron-in", null);
            Click((UIElement)typeof(SearchWindow).GetField("chip", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window));
            Step("scope-9-chip-out", null);
            app.Shutdown();
        }));
        app.Run();
    }

    static void Step(string name, string text, params Key[] keys)
    {
        if (text != null)
        {
            box.Text = text;
            box.CaretIndex = text.Length;
        }
        Pump(400); // search runs on the thread pool and renders back on the dispatcher
        var source = HwndSource.FromVisual(window) ?? HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        foreach (var key in keys)
        {
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Pump(250);
        }
        Render(Path.Combine(outDir, "ui-" + name + ".png"));
        Console.WriteLine("{0}: rendered", name);
    }

    static void Click(UIElement element)
    {
        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent, Source = element });
    }

    static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += delegate
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    static void Render(string path)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(window.Width, double.PositiveInfinity));
        content.Arrange(new Rect(0, 0, window.Width, content.DesiredSize.Height));
        int w = (int)window.Width + 40, h = (int)content.DesiredSize.Height + 40;

        // Stand-in for the blurred desktop behind the glass.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x3A, 0x55, 0x7A), Color.FromRgb(0x6A, 0x4A, 0x6E), 30), null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(20, 20, content.ActualWidth, content.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(path)) encoder.Save(file);
    }
}
