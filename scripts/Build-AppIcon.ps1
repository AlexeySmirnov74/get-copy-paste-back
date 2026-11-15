[CmdletBinding()]
param([string]$Source, [string]$Output, [string]$PreviewDirectory)

$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$Source) { $Source = Join-Path $project 'assets\HorizonClipboard.svg' }
if (!$Output) { $Output = Join-Path $project 'assets\HorizonClient.ico' }
if (!$PreviewDirectory) { $PreviewDirectory = Join-Path $project 'build\icon-preview' }

# Offline source-to-icon generation. The SVG uses only solid rounded rectangles
# and polygons so System.Drawing can render the canonical shapes directly.
# No application, clipboard listener, device or native component is started.
Add-Type -AssemblyName System.Drawing
if (!('HorizonIconBuilder' -as [type])) {
    Add-Type -ReferencedAssemblies System.Drawing,System.Xml -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Xml;

public static class HorizonIconBuilder
{
    private static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };

    public static void Build(string source, string output, string previews)
    {
        XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        XmlDocument document = new XmlDocument { XmlResolver = null };
        using (XmlReader reader = XmlReader.Create(source, settings)) document.Load(reader);
        XmlElement svg = document.DocumentElement;
        if (svg == null || svg.LocalName != "svg" || svg.GetAttribute("viewBox") != "0 0 256 256")
            throw new InvalidDataException("Expected a 256 by 256 SVG viewBox.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
        Directory.CreateDirectory(previews);
        List<byte[]> frames = new List<byte[]>();
        foreach (int size in Sizes)
        {
            using (Bitmap bitmap = Render(svg, size))
            {
                frames.Add(EncodeDib(bitmap));
                bitmap.Save(Path.Combine(previews, size.ToString(CultureInfo.InvariantCulture) + ".png"), ImageFormat.Png);
            }
        }
        using (FileStream stream = File.Create(output))
        using (BinaryWriter writer = new BinaryWriter(stream))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)Sizes.Length);
            int offset = 6 + 16 * Sizes.Length;
            for (int index = 0; index < Sizes.Length; index++)
            {
                writer.Write((byte)(Sizes[index] == 256 ? 0 : Sizes[index]));
                writer.Write((byte)(Sizes[index] == 256 ? 0 : Sizes[index]));
                writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(frames[index].Length); writer.Write(offset);
                offset += frames[index].Length;
            }
            foreach (byte[] frame in frames) writer.Write(frame);
        }
        Preview(svg, Path.Combine(previews, "contact-sheet.png"));
    }

    private static Bitmap Render(XmlElement svg, int size)
    {
        const int scale = 4;
        using (Bitmap large = new Bitmap(size * scale, size * scale, PixelFormat.Format32bppArgb))
        {
            using (Graphics graphics = Graphics.FromImage(large))
            {
                graphics.Clear(Color.Transparent);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.ScaleTransform(size * scale / 256f, size * scale / 256f);
                foreach (XmlNode node in svg.ChildNodes)
                {
                    XmlElement shape = node as XmlElement;
                    if (shape == null || shape.LocalName == "title" || shape.LocalName == "desc") continue;
                    if (shape.HasAttribute("transform") || shape.HasAttribute("style") || shape.HasAttribute("opacity"))
                        throw new InvalidDataException("Use explicit solid SVG geometry and fills.");
                    string fill = shape.GetAttribute("fill");
                    if (fill.Length != 7 || fill[0] != '#') throw new InvalidDataException("Expected an RGB fill.");
                    using (SolidBrush brush = new SolidBrush(ColorTranslator.FromHtml(fill)))
                    {
                        if (shape.LocalName == "rect")
                        {
                            float x = Number(shape, "x"), y = Number(shape, "y"), width = Number(shape, "width"), height = Number(shape, "height");
                            float radius = Number(shape, "rx");
                            if (width <= 0 || height <= 0 || radius <= 0 || radius * 2 > Math.Min(width, height))
                                throw new InvalidDataException("Invalid rounded rectangle.");
                            using (GraphicsPath path = new GraphicsPath())
                            {
                                float diameter = radius * 2;
                                path.AddArc(x, y, diameter, diameter, 180, 90);
                                path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
                                path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
                                path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
                                path.CloseFigure(); graphics.FillPath(brush, path);
                            }
                        }
                        else if (shape.LocalName == "polygon")
                        {
                            string[] coordinates = shape.GetAttribute("points").Split(new[] { ' ', ',', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                            if (coordinates.Length < 6 || coordinates.Length % 2 != 0) throw new InvalidDataException("Invalid polygon.");
                            PointF[] points = new PointF[coordinates.Length / 2];
                            for (int index = 0; index < points.Length; index++)
                                points[index] = new PointF(Single.Parse(coordinates[index * 2], CultureInfo.InvariantCulture), Single.Parse(coordinates[index * 2 + 1], CultureInfo.InvariantCulture));
                            graphics.FillPolygon(brush, points);
                        }
                        else throw new InvalidDataException("Unsupported SVG shape: " + shape.LocalName);
                    }
                }
            }
            Bitmap bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                using (ImageAttributes attributes = new ImageAttributes())
                {
                    attributes.SetWrapMode(WrapMode.TileFlipXY);
                    graphics.DrawImage(large, new Rectangle(0, 0, size, size), 0, 0, large.Width, large.Height, GraphicsUnit.Pixel, attributes);
                }
            }
            return bitmap;
        }
    }

    private static float Number(XmlElement shape, string name)
    { return Single.Parse(shape.GetAttribute(name), CultureInfo.InvariantCulture); }

    private static byte[] EncodeDib(Bitmap bitmap)
    {
        int size = bitmap.Width, maskStride = ((size + 31) / 32) * 4;
        using (MemoryStream stream = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(stream))
        {
            writer.Write(40); writer.Write(size); writer.Write(size * 2);
            writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(0);
            writer.Write(size * size * 4 + maskStride * size);
            writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
            // ICO DIB rows are bottom-up BGRA. Keep alpha and a conventional
            // padded AND mask, including in the 256px Explorer frame.
            for (int y = size - 1; y >= 0; y--)
                for (int x = 0; x < size; x++)
                {
                    Color color = bitmap.GetPixel(x, y);
                    writer.Write(color.B); writer.Write(color.G); writer.Write(color.R); writer.Write(color.A);
                }
            for (int y = size - 1; y >= 0; y--)
            {
                byte[] mask = new byte[maskStride];
                for (int x = 0; x < size; x++)
                    if (bitmap.GetPixel(x, y).A == 0) mask[x / 8] |= (byte)(128 >> (x % 8));
                writer.Write(mask);
            }
            return stream.ToArray();
        }
    }

    private static void Preview(XmlElement svg, string output)
    {
        int[] sizes = { 16, 20, 24, 32, 48, 64, 128 };
        using (Bitmap sheet = new Bitmap(840, 360, PixelFormat.Format32bppArgb))
        using (Graphics graphics = Graphics.FromImage(sheet))
        using (Font font = new Font("Segoe UI", 10, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            graphics.Clear(Color.FromArgb(244, 246, 244));
            using (SolidBrush dark = new SolidBrush(Color.FromArgb(29, 37, 31))) graphics.FillRectangle(dark, 0, 180, 840, 180);
            for (int row = 0; row < 2; row++)
                for (int index = 0; index < sizes.Length; index++)
                {
                    int size = sizes[index], x = index * 120 + (120 - size) / 2, y = row * 180 + (144 - size) / 2 + 6;
                    using (Bitmap icon = Render(svg, size)) graphics.DrawImageUnscaled(icon, x, y);
                    using (SolidBrush text = new SolidBrush(row == 0 ? Color.FromArgb(45, 58, 47) : Color.FromArgb(223, 233, 224)))
                        graphics.DrawString(size.ToString(CultureInfo.InvariantCulture) + "px", font, text, index * 120 + 48, row * 180 + 155);
                }
            sheet.Save(output, ImageFormat.Png);
        }
    }
}
'@
}
[HorizonIconBuilder]::Build([IO.Path]::GetFullPath($Source), [IO.Path]::GetFullPath($Output), [IO.Path]::GetFullPath($PreviewDirectory))
Write-Output ('Shared application icon generated: '+$Output+'; 10 RGBA frames, 16/20/24/32/40/48/64/96/128/256px.')
Write-Output ('Preview: '+(Join-Path $PreviewDirectory 'contact-sheet.png'))
