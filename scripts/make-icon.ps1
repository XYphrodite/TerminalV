#Requires -Version 5.1
<#
.SYNOPSIS
    Draws the TerminalV V-and-cursor icon at native Windows icon sizes.
    Optionally writes a preview on light and dark backgrounds.
#>
[CmdletBinding()]
param(
    [string] $OutPath,
    [string] $PreviewPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $OutPath) {
    $OutPath = Join-Path (Split-Path $PSScriptRoot) 'src\TerminalV\Assets\TerminalV.ico'
}
Add-Type -AssemblyName System.Drawing

$code = @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Collections.Generic;

public static class TerminalVIcon
{
    static readonly Color Bg = Color.FromArgb(22, 28, 38);
    static readonly Color Blue = Color.FromArgb(85, 177, 255);

    public static void Write(string outPath, int[] sizes)
    {
        var pngs = new List<byte[]>();
        foreach (var size in sizes)
        {
            using (var bmp = Render(size))
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                pngs.Add(ms.ToArray());
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
        File.WriteAllBytes(outPath, PackIco(pngs, sizes));
    }

    static Bitmap Render(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            Draw(g, size);
        }
        return bmp;
    }

    static void Draw(Graphics g, int size)
    {
        var radius = size * 0.23f;
        using (var bg = new SolidBrush(Bg))
            FillRound(g, bg, 0, 0, size, size, radius);

        DrawMark(g, size);
    }

    static void DrawMark(Graphics g, int size)
    {
        // A custom, font-independent V with a flat baseline. The complete V_
        // mark is optically centered as a unit, not as two separate glyphs.
        var pts = new[]
        {
            new PointF(size * 0.17f, size * 0.26f),
            new PointF(size * 0.285f, size * 0.26f),
            new PointF(size * 0.41f, size * 0.61f),
            new PointF(size * 0.535f, size * 0.26f),
            new PointF(size * 0.65f, size * 0.26f),
            new PointF(size * 0.46f, size * 0.74f),
            new PointF(size * 0.36f, size * 0.74f)
        };
        using (var blue = new SolidBrush(Blue))
        {
            g.FillPolygon(blue, pts);

            // Snap the cursor to whole pixels, keeping it crisp at 16/20px.
            var left = (float)Math.Round(size * 0.65f);
            var right = (float)Math.Round(size * 0.85f);
            var bottom = (float)Math.Round(size * 0.74f);
            var height = (float)Math.Max(2, Math.Round(size * 0.10f));
            g.SmoothingMode = SmoothingMode.None;
            g.FillRectangle(blue, left, bottom - height, right - left, height);
        }
    }

    public static void WritePreview(string outPath)
    {
        using (var bmp = new Bitmap(760, 400, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(bmp))
        using (var titleFont = new Font("Segoe UI", 22, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var labelFont = new Font("Segoe UI", 13, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var light = new SolidBrush(Color.FromArgb(233, 238, 247)))
        using (var muted = new SolidBrush(Color.FromArgb(157, 171, 190)))
        using (var dark = new SolidBrush(Bg))
        {
            g.Clear(Color.FromArgb(11, 14, 20));
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (var icon = Render(256)) g.DrawImageUnscaled(icon, 40, 72);
            g.DrawString("TerminalV", titleFont, light, 340, 44);
            g.DrawString("V + cursor / native pixel sizes", labelFont, muted, 342, 79);

            var sizes = new[] { 16, 20, 24, 32, 48 };
            for (var row = 0; row < 2; row++)
            {
                var top = 124 + row * 122;
                g.FillRectangle(row == 0 ? dark : light, 336, top, 388, 104);
                for (var i = 0; i < sizes.Length; i++)
                {
                    var center = 374 + i * 77;
                    using (var icon = Render(sizes[i]))
                        g.DrawImageUnscaled(icon, center - sizes[i] / 2, top + 8 + (48 - sizes[i]) / 2);
                    var label = sizes[i] + " px";
                    var width = g.MeasureString(label, labelFont).Width;
                    g.DrawString(label, labelFont, row == 0 ? light : dark, center - width / 2, top + 71);
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
            bmp.Save(outPath, ImageFormat.Png);
        }
    }

    static void FillRound(Graphics g, Brush brush, float x, float y, float w, float h, float r)
    {
        r = Math.Min(r, Math.Min(w, h) / 2f);
        using (var path = new GraphicsPath())
        {
            var d = r * 2f;
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + w - d, y, d, d, 270, 90);
            path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            path.AddArc(x, y + h - d, d, d, 90, 90);
            path.CloseFigure();
            g.FillPath(brush, path);
        }
    }

    static byte[] PackIco(List<byte[]> pngs, int[] sizes)
    {
        var count = pngs.Count;
        var header = 6 + 16 * count;
        var offset = header;
        using (var ms = new MemoryStream())
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write((ushort)0);
            bw.Write((ushort)1);
            bw.Write((ushort)count);
            var offsets = new int[count];
            for (var i = 0; i < count; i++)
            {
                offsets[i] = offset;
                offset += pngs[i].Length;
            }
            for (var i = 0; i < count; i++)
            {
                var dim = sizes[i] >= 256 ? 0 : sizes[i];
                bw.Write((byte)dim);
                bw.Write((byte)dim);
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write((ushort)1);
                bw.Write((ushort)32);
                bw.Write(pngs[i].Length);
                bw.Write(offsets[i]);
            }
            foreach (var png in pngs)
                bw.Write(png);
            return ms.ToArray();
        }
    }
}
'@

Add-Type -TypeDefinition $code -ReferencedAssemblies System.Drawing

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
[TerminalVIcon]::Write($OutPath, $sizes)
Write-Host "Wrote $OutPath"
if ($PreviewPath) {
    [TerminalVIcon]::WritePreview($PreviewPath)
    Write-Host "Wrote $PreviewPath"
}
