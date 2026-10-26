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

            // Brand mark: a boost gauge on a graphite tile; the needle rests
            // in the amber Boost zone.
            float scale = size / 256F;
            var bounds = new RectangleF(8 * scale, 8 * scale, 240 * scale, 240 * scale);
            using (GraphicsPath frame = RoundedRectangle(bounds, 56 * scale))
            using (var background = new SolidBrush(Color.FromArgb(34, 39, 45)))
            using (var edge = new Pen(Color.FromArgb(53, 60, 68), Math.Max(1F, 6F * scale)))
            {
                graphics.FillPath(background, frame);
                graphics.DrawPath(edge, frame);
            }

            float centerX = 128F * scale;
            float centerY = 148F * scale;
            float radius = 78F * scale;
            var arcBounds = new RectangleF(centerX - radius, centerY - radius, radius * 2, radius * 2);
            using (var scalePen = new Pen(Color.FromArgb(138, 146, 156), Math.Max(1.2F, 18F * scale)))
            using (var zonePen = new Pen(Color.FromArgb(255, 160, 48), Math.Max(1.6F, 24F * scale)))
            using (var needlePen = new Pen(Color.FromArgb(237, 239, 242), Math.Max(1.4F, 18F * scale)))
            using (var hub = new SolidBrush(Color.FromArgb(255, 160, 48)))
            {
                scalePen.StartCap = LineCap.Round;
                scalePen.EndCap = LineCap.Round;
                zonePen.StartCap = LineCap.Round;
                zonePen.EndCap = LineCap.Round;
                needlePen.StartCap = LineCap.Round;
                needlePen.EndCap = LineCap.Round;
                // GDI+ measures arcs clockwise from +X; the gauge measures from up.
                graphics.DrawArc(scalePen, arcBounds, -120F - 90F, 175F);
                graphics.DrawArc(zonePen, arcBounds, 62F - 90F, 58F);
                double needle = 78 * Math.PI / 180;
                graphics.DrawLine(
                    needlePen,
                    centerX,
                    centerY,
                    centerX + (float)(radius * 0.92 * Math.Sin(needle)),
                    centerY - (float)(radius * 0.92 * Math.Cos(needle)));
                float hubRadius = Math.Max(1.5F, 20F * scale);
                graphics.FillEllipse(hub, centerX - hubRadius, centerY - hubRadius, hubRadius * 2, hubRadius * 2);
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
