using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

internal static class GenerateIcon
{
    private static void Main(string[] args)
    {
        if (args.Length != 1)
        {
            throw new ArgumentException("Output .ico path is required.");
        }

        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        var images = new List<byte[]>();
        foreach (int size in sizes)
        {
            using (Bitmap bitmap = DrawIcon(size))
            using (var stream = new MemoryStream())
            {
                bitmap.Save(stream, ImageFormat.Png);
                images.Add(stream.ToArray());
            }
        }

        using (var output = new FileStream(args[0], FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(output))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)sizes.Length);
            int dataOffset = 6 + sizes.Length * 16;
            for (int index = 0; index < sizes.Length; index++)
            {
                writer.Write((byte)(sizes[index] == 256 ? 0 : sizes[index]));
                writer.Write((byte)(sizes[index] == 256 ? 0 : sizes[index]));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(images[index].Length);
                writer.Write(dataOffset);
                dataOffset += images[index].Length;
            }
            foreach (byte[] image in images)
            {
                writer.Write(image);
            }
        }
    }

    private static Bitmap DrawIcon(int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.Clear(Color.Transparent);

            // Brand mark: violet-to-fuchsia rounded square with a lightning bolt.
            float scale = size / 256F;
            var bounds = new RectangleF(8 * scale, 8 * scale, 240 * scale, 240 * scale);
            using (GraphicsPath frame = RoundedRectangle(bounds, 60 * scale))
            using (var background = new LinearGradientBrush(
                bounds,
                Color.FromArgb(124, 58, 237),
                Color.FromArgb(192, 38, 211),
                45F))
            using (var highlight = new Pen(Color.FromArgb(70, 255, 255, 255), Math.Max(1F, 4F * scale)))
            {
                graphics.FillPath(background, frame);
                if (size >= 32)
                {
                    graphics.DrawPath(highlight, frame);
                }
            }

            PointF[] grid =
            {
                new PointF(13F, 2.5F),
                new PointF(4F, 13.5F),
                new PointF(11.5F, 13.5F),
                new PointF(10.5F, 21.5F),
                new PointF(20F, 10F),
                new PointF(12.5F, 10F)
            };
            float unit = 9F * scale;
            var bolt = new PointF[grid.Length];
            for (int index = 0; index < grid.Length; index++)
            {
                bolt[index] = new PointF(
                    128F * scale + (grid[index].X - 12F) * unit,
                    128F * scale + (grid[index].Y - 12F) * unit);
            }
            using (var path = new GraphicsPath())
            using (var brush = new SolidBrush(Color.White))
            using (var outline = new Pen(Color.White, Math.Max(1F, 10F * scale)))
            {
                outline.LineJoin = LineJoin.Round;
                path.AddPolygon(bolt);
                graphics.FillPath(brush, path);
                graphics.DrawPath(outline, path);
            }
        }
        return bitmap;
    }

    private static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
    {
        float diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
