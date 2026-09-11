#Requires -Version 5.1
<#
.SYNOPSIS
    Draws TerminalV.ico (multi-size) matching the in-app V badge.
#>
[CmdletBinding()]
param(
    [string] $OutPath
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
    static readonly Color Bg = Color.FromArgb(11, 13, 16);
    static readonly Color Blue = Color.FromArgb(61, 158, 255);
    static readonly Color Ink = Color.FromArgb(7, 16, 24);

    public static void Write(string outPath, int[] sizes)
    {
        var pngs = new List<byte[]>();
        foreach (var size in sizes)
        {
            using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            using (var ms = new MemoryStream())
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);
                Draw(g, size);
                bmp.Save(ms, ImageFormat.Png);
                pngs.Add(ms.ToArray());
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outPath));
        File.WriteAllBytes(outPath, PackIco(pngs, sizes));
    }

    static void Draw(Graphics g, int size)
    {
        var pad = Math.Max(1f, size * 0.06f);
        var radius = Math.Max(2f, size * 0.22f);
        using (var bg = new SolidBrush(Bg))
            FillRound(g, bg, 0, 0, size, size, radius);

        var inner = pad * 1.6f;
        var tile = size - inner * 2f;
        var tileRadius = Math.Max(1.5f, tile * 0.22f);
        using (var blue = new SolidBrush(Blue))
            FillRound(g, blue, inner, inner, tile, tile, tileRadius);

        DrawV(g, inner, inner, tile, size);
    }

    static void DrawV(Graphics g, float x, float y, float tile, int size)
    {
        var w = tile;
        var h = tile;
        var pts = new[]
        {
            new PointF(x + w * 0.22f, y + h * 0.20f),
            new PointF(x + w * 0.38f, y + h * 0.20f),
            new PointF(x + w * 0.50f, y + h * 0.58f),
            new PointF(x + w * 0.62f, y + h * 0.20f),
            new PointF(x + w * 0.78f, y + h * 0.20f),
            new PointF(x + w * 0.50f, y + h * 0.82f)
        };
        using (var ink = new SolidBrush(Ink))
            g.FillPolygon(ink, pts);
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
