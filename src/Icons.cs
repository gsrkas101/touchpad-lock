// Tray and pill icons.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace TouchpadLock
{
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

        // For the pill: the icon drawn at the colour's transparency.
        public static void PaintForPill(Graphics g, RectangleF r, Color color, bool locked)
        {
            using (Bitmap bmp = Render(Color.FromArgb(255, color), locked, (int)Math.Round(r.Width)))
            using (var fade = new ImageAttributes())
            {
                fade.SetColorMatrix(new ColorMatrix { Matrix33 = color.A / 255f });
                g.DrawImage(bmp, new Rectangle((int)Math.Round(r.X), (int)Math.Round(r.Y), bmp.Width, bmp.Height),
                    0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, fade);
            }
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
