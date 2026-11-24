// The "Touchpad locked" pill and the icons.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace TouchpadLock
{
    // Black pill at the top-centre of the screen, in the style of a Dynamic Island. A per-pixel-alpha
    // layered window, so the pill has smooth edges. Never takes focus. Sits below Keyboard Lock's pill
    // when both are showing.
    sealed class IslandForm : Form
    {
        const int ExpandMs = 480, MorphMs = 360, CollapseMs = 240, SlideMs = 260, UnlockedHoldMs = 1300;
        const string KeyboardPillTitle = "Keyboard Lock - keyboard locked";

        readonly float scale;
        readonly Font titleFont, hintFont;
        readonly System.Windows.Forms.Timer hold = new System.Windows.Forms.Timer { Interval = UnlockedHoldMs };
        readonly System.Windows.Forms.Timer neighbour = new System.Windows.Forms.Timer { Interval = 120 };
        readonly Stopwatch clock = new Stopwatch(), slideClock = new Stopwatch();

        string title = "", hint = "";
        float titleWidth, hintWidth;
        Bitmap icon;
        bool showingLocked, collapsing, animating, stepQueued;
        float width, fromWidth, toWidth, alpha, fromAlpha, toAlpha, contentAlpha;
        float drop, fromDrop, toDrop;
        int durationMs;

        public event EventHandler UnlockRequested, HotKeyPressed;

        public IslandForm()
        {
            Text = "Touchpad Lock - touchpad locked";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Cursor = Cursors.Hand;
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
            titleFont = new Font("Segoe UI Semibold", 14f * scale, GraphicsUnit.Pixel);
            hintFont = new Font("Segoe UI", 12.5f * scale, GraphicsUnit.Pixel);
            hold.Tick += (s, e) => Collapse();
            neighbour.Tick += (s, e) => FollowNeighbour();
        }

        float S(float v) { return v * scale; }
        float PillHeight { get { return S(40); } }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && HotKeyPressed != null) HotKeyPressed(this, EventArgs.Empty);
            base.WndProc(ref m);
        }

        public static bool KeyboardPillVisible()
        {
            IntPtr hwnd = Native.FindWindow(null, KeyboardPillTitle);
            return hwnd != IntPtr.Zero && Native.IsWindowVisible(hwnd);
        }

        public void KeepOnTop()
        {
            if (Visible) Native.KeepOnTop(Handle);
        }

        public void ShowLocked(string hintText)
        {
            hold.Stop();
            showingLocked = true;
            SetContent("Touchpad locked", hintText, TrayApp.LockedColor, true);
            AnimateTo(ContentWidth(), 1f, Visible && !collapsing ? MorphMs : ExpandMs);
        }

        // Only when the pill is on screen: morph to "Touchpad unlocked", then shrink away.
        public void ShowUnlocked()
        {
            showingLocked = false;
            if (!Visible || collapsing) return;
            SetContent("Touchpad unlocked", "", TrayApp.UnlockedColor, false);
            AnimateTo(ContentWidth(), 1f, MorphMs);
            hold.Stop();
            hold.Start();
        }

        void Collapse()
        {
            hold.Stop();
            if (!Visible || collapsing) return;
            AnimateTo(PillHeight, 0f, CollapseMs);
        }

        void SetContent(string newTitle, string newHint, Color color, bool closed)
        {
            title = newTitle;
            hint = newHint;
            if (icon != null) icon.Dispose();
            icon = Icons.Render(color, closed, (int)S(22));
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                titleWidth = g.MeasureString(title, titleFont, PointF.Empty, StringFormat.GenericTypographic).Width;
                hintWidth = hint.Length == 0 ? 0 : g.MeasureString(hint, hintFont, PointF.Empty, StringFormat.GenericTypographic).Width;
            }
        }

        float ContentWidth()
        {
            return S(46) + titleWidth + (hintWidth > 0 ? S(12) + hintWidth : 0) + S(20);
        }

        float DropTarget() { return KeyboardPillVisible() ? PillHeight + S(8) : 0f; }

        void FollowNeighbour()
        {
            float target = DropTarget();
            if (target == toDrop) return;
            fromDrop = drop;
            toDrop = target;
            slideClock.Restart();
            StartFrames();
        }

        void AnimateTo(float targetWidth, float targetAlpha, int ms)
        {
            if (!Visible)
            {
                width = PillHeight;   // start as a small dot, like the island growing out of nothing
                alpha = 0;
                drop = fromDrop = toDrop = DropTarget();
            }
            fromWidth = width;
            fromAlpha = alpha;
            toWidth = targetWidth;
            toAlpha = targetAlpha;
            durationMs = ms;
            collapsing = targetAlpha <= 0;
            contentAlpha = 0;
            animating = true;
            clock.Restart();
            if (!Visible)
            {
                Bounds = PillBounds();
                Render();
                Show();
                Native.KeepOnTop(Handle);
                neighbour.Start();
            }
            StartFrames();
        }

        // One frame per screen refresh: draw, wait for the compositor, queue the next frame.
        void StartFrames()
        {
            if (stepQueued || IsDisposed) return;
            stepQueued = true;
            BeginInvoke((Action)Step);
        }

        void Step()
        {
            stepQueued = false;
            if (IsDisposed || !Visible) return;

            if (drop != toDrop)
            {
                float s = Math.Min(1f, (float)slideClock.Elapsed.TotalMilliseconds / SlideMs);
                float u = 1f - s;
                drop = s >= 1f ? toDrop : fromDrop + (toDrop - fromDrop) * (1f - u * u * u);
            }

            float t = Math.Min(1f, (float)clock.Elapsed.TotalMilliseconds / Math.Max(1, durationMs));
            if (animating)
            {
                if (collapsing)
                {
                    width = fromWidth + (toWidth - fromWidth) * t * t * t;
                    alpha = fromAlpha + (toAlpha - fromAlpha) * t;
                    contentAlpha = Math.Max(0f, 1f - t * 3f);
                }
                else
                {
                    width = Math.Max(PillHeight, fromWidth + (toWidth - fromWidth) * EaseOutBack(t));
                    alpha = fromAlpha + (toAlpha - fromAlpha) * Math.Min(1f, t * 3f);
                    contentAlpha = Math.Max(0f, Math.Min(1f, (t - 0.2f) / 0.5f));
                }
            }
            Render();

            if (animating && t >= 1f)
            {
                animating = false;
                if (collapsing)
                {
                    collapsing = false;
                    neighbour.Stop();
                    Hide();
                    return;
                }
            }
            if (animating || drop != toDrop)
            {
                Native.DwmFlush();
                StartFrames();
            }
        }

        // Slight overshoot, so the pill springs open.
        static float EaseOutBack(float t)
        {
            const float c1 = 1.2f, c3 = c1 + 1f;
            float u = t - 1f;
            return 1f + c3 * u * u * u + c1 * u * u;
        }

        Rectangle PillBounds()
        {
            int w = (int)Math.Ceiling(width), h = (int)Math.Ceiling(PillHeight);
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            return new Rectangle(area.Left + (area.Width - w) / 2, area.Top + (int)Math.Round(S(10) + drop), w, h);
        }

        void Render()
        {
            Rectangle bounds = PillBounds();
            using (var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                using (GraphicsPath pill = Pill(new RectangleF(0, 0, width, PillHeight)))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    g.FillPath(Brushes.Black, pill);
                    g.SetClip(pill);   // content is revealed as the pill grows
                    int a = (int)(contentAlpha * 255);
                    if (a > 0)
                    {
                        using (var fade = new ImageAttributes())
                        {
                            fade.SetColorMatrix(new ColorMatrix { Matrix33 = contentAlpha });
                            g.DrawImage(icon, new Rectangle((int)S(15), (int)((PillHeight - icon.Height) / 2), icon.Width, icon.Height),
                                0, 0, icon.Width, icon.Height, GraphicsUnit.Pixel, fade);
                        }
                        float x = S(46);
                        using (var brush = new SolidBrush(Color.FromArgb(a, Color.White)))
                            g.DrawString(title, titleFont, brush, x, TextTop(titleFont), StringFormat.GenericTypographic);
                        if (hint.Length > 0)
                            using (var brush = new SolidBrush(Color.FromArgb(a, 150, 150, 158)))
                                g.DrawString(hint, hintFont, brush, x + titleWidth + S(12), TextTop(hintFont),
                                    StringFormat.GenericTypographic);
                    }
                }
                Native.SetLayeredBitmap(Handle, bmp, bounds.X, bounds.Y, (byte)(Math.Max(0f, Math.Min(1f, alpha)) * 255));
            }
        }

        float TextTop(Font font)
        {
            return (PillHeight - font.Size * 1.33f) / 2f;   // Segoe UI line height is about 1.33 em
        }

        static GraphicsPath Pill(RectangleF r)
        {
            float d = r.Height;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 90, 180);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
            path.CloseFigure();
            return path;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left && showingLocked && UnlockRequested != null) UnlockRequested(this, EventArgs.Empty);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                hold.Dispose();
                neighbour.Dispose();
                titleFont.Dispose();
                hintFont.Dispose();
                if (icon != null) icon.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    sealed class OwnedIcon : IDisposable
    {
        public readonly Icon Icon;
        IntPtr handle;

        public OwnedIcon(IntPtr handle)
        {
            this.handle = handle;
            Icon = Icon.FromHandle(handle);
        }

        public void Dispose()
        {
            if (handle == IntPtr.Zero) return;
            Icon.Dispose();
            Native.DestroyIcon(handle);
            handle = IntPtr.Zero;
        }
    }

    static class Icons
    {
        public static OwnedIcon Make(Color color, bool locked, int size)
        {
            using (Bitmap bmp = Render(color, locked, size)) return new OwnedIcon(bmp.GetHicon());
        }

        // A touchpad with its two buttons; crossed out when locked.
        public static Bitmap Render(Color color, bool locked, int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float w = size, stroke = Math.Max(1.5f, w * 0.1f), half = stroke / 2f;
                var pad = new RectangleF(w * 0.03f + half, w * 0.14f + half, w * 0.94f - stroke, w * 0.72f - stroke);
                float split = pad.Y + pad.Height * 0.64f;
                using (var pen = new Pen(color, stroke))
                {
                    using (GraphicsPath path = RoundedRect(pad, w * 0.15f)) g.DrawPath(pen, path);
                    g.DrawLine(pen, pad.X, split, pad.Right, split);
                    g.DrawLine(pen, pad.X + pad.Width / 2f, split, pad.X + pad.Width / 2f, pad.Bottom);
                }
                if (locked)
                {
                    var a = new PointF(w * 0.12f, w * 0.06f);
                    var b = new PointF(w * 0.88f, w * 0.94f);
                    g.CompositingMode = CompositingMode.SourceCopy;   // clear a gap around the slash
                    using (var gap = new Pen(Color.Transparent, stroke * 2.8f)) g.DrawLine(gap, a, b);
                    g.CompositingMode = CompositingMode.SourceOver;
                    using (var pen = new Pen(color, stroke * 1.25f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawLine(pen, a, b);
                }
            }
            return bmp;
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

        // Writes a multi-size .ico (PNG entries), used as the exe icon.
        public static void ExportIco(string path)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
            var images = new List<byte[]>();
            foreach (int size in sizes)
                using (Bitmap bmp = Render(TrayApp.LockedColor, true, size))
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    images.Add(ms.ToArray());
                }
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write((short)0);
                w.Write((short)1);
                w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((short)0);
                    w.Write((short)1);
                    w.Write((short)32);
                    w.Write(images[i].Length);
                    w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (byte[] image in images) w.Write(image);
            }
        }
    }
}
