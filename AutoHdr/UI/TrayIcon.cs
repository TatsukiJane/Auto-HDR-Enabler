using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using AutoHdr.Core;
using Forms = System.Windows.Forms;

namespace AutoHdr.UI;

/// <summary>Значок в трее: показывает режим SDR/HDR, по щелчку открывает окно.</summary>
public sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon icon;
    readonly Forms.ToolStripMenuItem toggleItem;
    readonly Forms.ToolStripMenuItem pauseItem;
    readonly Icon hdrIcon, sdrIcon, pausedIcon;

    public TrayIcon(App app)
    {
        try { Forms.Application.SetColorMode(Forms.SystemColorMode.System); } catch { }

        int size = Math.Max(16, Forms.SystemInformation.SmallIconSize.Width);
        hdrIcon = IconFactory.Badge("HDR", Color.FromArgb(245, 158, 11), Color.FromArgb(28, 25, 23), size);
        sdrIcon = IconFactory.Badge("SDR", Color.FromArgb(107, 114, 128), Color.White, size);
        pausedIcon = IconFactory.Badge("II", Color.FromArgb(75, 85, 99), Color.FromArgb(229, 231, 235), size);

        var open = new Forms.ToolStripMenuItem("Открыть Auto HDR", null, (_, _) => app.ShowMainWindow());
        open.Font = new Font(open.Font, System.Drawing.FontStyle.Bold);
        toggleItem = new Forms.ToolStripMenuItem("Включить HDR", null, async (_, _) => await app.ToggleHdrAsync());
        pauseItem = new Forms.ToolStripMenuItem("Приостановить автопереключение", null, (_, _) => app.SetPaused(!app.Settings.Paused));
        var exit = new Forms.ToolStripMenuItem("Выйти", null, (_, _) => app.ExitApp());

        var menu = new Forms.ContextMenuStrip();
        menu.Items.AddRange([open, new Forms.ToolStripSeparator(), toggleItem, pauseItem, new Forms.ToolStripSeparator(), exit]);

        icon = new Forms.NotifyIcon
        {
            Icon = sdrIcon,
            Text = "Auto HDR",
            ContextMenuStrip = menu,
            Visible = true,
        };
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) app.ShowMainWindow();
        };
        icon.BalloonTipClicked += (_, _) => app.ShowMainWindow();
    }

    public void Update(bool? hdrOn, bool paused, IReadOnlyList<string> runningGames)
    {
        icon.Icon = paused ? pausedIcon : hdrOn == true ? hdrIcon : sdrIcon;

        var text = hdrOn switch
        {
            true => "Auto HDR: HDR включён",
            false => "Auto HDR: HDR выключен",
            _ => "Auto HDR: нет мониторов с HDR",
        };
        if (paused) text += ", автопереключение на паузе";
        else if (runningGames.Count > 0) text += $"\nИгра: {string.Join(", ", runningGames)}";
        icon.Text = text.Length > 127 ? text[..126] + "…" : text;

        toggleItem.Text = hdrOn == true ? "Выключить HDR" : "Включить HDR";
        toggleItem.Enabled = hdrOn != null;
        pauseItem.Checked = paused;
    }

    public void ShowBalloon(string title, string text) => icon.ShowBalloonTip(3000, title, text, Forms.ToolTipIcon.None);

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
        hdrIcon.Dispose();
        sdrIcon.Dispose();
        pausedIcon.Dispose();
    }
}

public static class IconFactory
{
    /// <summary>Значок-плашка с коротким текстом, отрисованный под размер значков трея.</summary>
    public static Icon Badge(string text, Color background, Color foreground, int size)
    {
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            var rect = new RectangleF(0, size * 0.14f, size - 1, size * 0.72f);
            using (var path = RoundedRect(rect, size * 0.2f))
            using (var brush = new SolidBrush(background))
                g.FillPath(brush, path);

            float fontSize = text.Length >= 3 ? size * 0.40f : size * 0.48f;
            using var font = new Font("Segoe UI", fontSize, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var textBrush = new SolidBrush(foreground);
            g.DrawString(text, font, textBrush, new RectangleF(-1, 0, size + 2, size), format);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            Native.DestroyIcon(handle);
        }
    }

    static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
