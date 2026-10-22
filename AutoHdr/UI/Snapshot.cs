using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AutoHdr.Core;

namespace AutoHdr.UI;

/// <summary>
/// Служебный режим --snapshot &lt;папка&gt;: рисует каждую вкладку окна в PNG без показа на экране.
/// Нужен для проверки вёрстки без участия пользователя.
/// </summary>
public static class Snapshot
{
    public static void Render(App app, string directory)
    {
        Directory.CreateDirectory(directory);
        var window = new MainWindow(app)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.Show();

        string[] names = ["games", "monitors", "settings", "journal"];
        for (int i = 0; i < window.Tabs.Items.Count && i < names.Length; i++)
        {
            window.Tabs.SelectedIndex = i;
            Pump();
            var content = (FrameworkElement)window.Content;
            var dpi = VisualTreeHelper.GetDpi(window);
            int width = (int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX);
            int height = (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY);

            var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                // У Fluent-окна фон прозрачный (Mica), подставляем цвет фона темы
                var background = window.TryFindResource("ApplicationBackgroundBrush") as Brush
                                 ?? window.TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush
                                 ?? Brushes.White;
                dc.DrawRectangle(background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
                dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            }
            bitmap.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(directory, $"{i + 1}-{names[i]}.png");
            using var stream = File.Create(path);
            encoder.Save(stream);
            Log.Info(Loc.T($"Снимок вкладки сохранён: {path}", $"Tab snapshot saved: {path}"));
        }
        window.CloseForExit();
    }

    static void Pump()
    {
        for (int i = 0; i < 5; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(60);
        }
    }
}
