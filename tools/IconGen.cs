using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

// Draws Seek's icon (a magnifier with a blue "dot", as in ".pdf") into a multi-size .ico, and
// optionally the README banner in the same style.
//   IconGen.exe <output.ico> [preview.png] [banner.png]
static class IconGen
{
    static void Main(string[] args)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
        var images = new List<byte[]>();
        foreach (int size in sizes)
            using (var bitmap = Draw(size))
                images.Add(size == 256 ? Png(bitmap) : Dib(bitmap));

        using (var writer = new BinaryWriter(File.Create(args[0])))
        {
            writer.Write((short)0);
            writer.Write((short)1);
            writer.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((short)1);
                writer.Write((short)32);
                writer.Write(images[i].Length);
                writer.Write(offset);
                offset += images[i].Length;
            }
            foreach (var image in images) writer.Write(image);
        }

        if (args.Length > 1)
            using (var preview = Draw(256))
                preview.Save(args[1], ImageFormat.Png);
        if (args.Length > 2)
            using (var banner = Banner(1600, 400))
                banner.Save(args[2], ImageFormat.Png);
    }

    static Bitmap Draw(int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            float s = size;
            float inset = size >= 32 ? s * 0.04f : 0;
            Tile(g, new RectangleF(inset, inset, s - 2 * inset, s - 2 * inset), s * 0.22f);
            Glyph(g, 0, 0, s);
        }
        return bitmap;
    }

    /// <summary>A wide version of the icon: the same tile, the magnifier, and the name.</summary>
    static Bitmap Banner(int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        using (var nameFont = new Font("Segoe UI Semibold", height * 0.26f, GraphicsUnit.Pixel))
        using (var lineFont = new Font("Segoe UI", height * 0.085f, GraphicsUnit.Pixel))
        using (var white = new SolidBrush(Color.White))
        using (var grey = new SolidBrush(Color.FromArgb(255, 160, 168, 184)))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            Tile(g, new RectangleF(0, 0, width, height), height * 0.16f);

            const string name = "Seek";
            const string line = "Instant file search for Windows";
            var tight = StringFormat.GenericTypographic;
            SizeF nameSize = g.MeasureString(name, nameFont, int.MaxValue, tight);
            SizeF lineSize = g.MeasureString(line, lineFont, int.MaxValue, tight);

            // The magnifier fills about 0.2..0.8 of its square; centre it and the text as one group.
            float s = height * 0.78f;
            float glyphWidth = s * 0.6f, gap = height * 0.07f;
            float textWidth = Math.Max(nameSize.Width, lineSize.Width);
            float left = (width - (glyphWidth + gap + textWidth)) / 2;
            Glyph(g, left - s * 0.2f, (height - s) / 2, s);

            float textLeft = left + glyphWidth + gap;
            float textTop = (height - (nameSize.Height * 0.9f + lineSize.Height)) / 2;
            g.DrawString(name, nameFont, white, textLeft - height * 0.01f, textTop, tight);
            g.DrawString(line, lineFont, grey, textLeft, textTop + nameSize.Height * 0.9f, tight);
        }
        return bitmap;
    }

    static void Tile(Graphics g, RectangleF area, float radius)
    {
        using (var shape = Rounded(area, radius))
        using (var fill = new LinearGradientBrush(area, Color.FromArgb(255, 60, 66, 80), Color.FromArgb(255, 22, 24, 30), LinearGradientMode.Vertical))
            g.FillPath(fill, shape);
    }

    /// <summary>The white magnifier with the blue dot, drawn in the square at (x, y) of side s.</summary>
    static void Glyph(Graphics g, float x, float y, float s)
    {
        float cx = x + s * 0.44f, cy = y + s * 0.44f, r = s * 0.2f;
        float ring = Math.Max(1.4f, s * 0.08f);
        using (var pen = new Pen(Color.White, ring))
        {
            g.DrawEllipse(pen, cx - r, cy - r, 2 * r, 2 * r);
            pen.Width = Math.Max(1.8f, s * 0.11f);
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            float d = (r + ring * 0.5f) * 0.7071f;
            g.DrawLine(pen, cx + d, cy + d, x + s * 0.75f, y + s * 0.75f);
        }
        float dot = Math.Max(1.2f, s * 0.065f);
        using (var blue = new SolidBrush(Color.FromArgb(255, 77, 163, 255)))
            g.FillEllipse(blue, cx - dot, cy - dot, 2 * dot, 2 * dot);
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
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

    static byte[] Png(Bitmap bitmap)
    {
        using (var stream = new MemoryStream())
        {
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
    }

    /// <summary>Classic icon image: BITMAPINFOHEADER, bottom-up BGRA rows, empty AND mask.</summary>
    static byte[] Dib(Bitmap bitmap)
    {
        int w = bitmap.Width, h = bitmap.Height;
        int maskStride = (w + 31) / 32 * 4;
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(40);
            writer.Write(w);
            writer.Write(h * 2);
            writer.Write((short)1);
            writer.Write((short)32);
            writer.Write(0);
            writer.Write(w * h * 4 + maskStride * h);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            var data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var row = new byte[w * 4];
            for (int y = h - 1; y >= 0; y--)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                writer.Write(row);
            }
            bitmap.UnlockBits(data);
            writer.Write(new byte[maskStride * h]);
            writer.Flush();
            return stream.ToArray();
        }
    }
}
