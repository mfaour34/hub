using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// Renders the Hub logo "h." (same geometry as assets/hub-logo.svg, 256-unit canvas) into a multi-size .ico:
// the Instrument theme's dark tile, a light geometric h and the orange accent dot. Drawn as shapes, not a font,
// so it is identical everywhere and stays crisp at tray size.
var output = args.Length > 0 ? args[0] : "Hub.ico";
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var pngs = sizes.Select(s => (Size: s, Png: Render(s))).ToList();

using (var fs = File.Create(output))
using (var w = new BinaryWriter(fs))
{
    w.Write((short)0); w.Write((short)1); w.Write((short)pngs.Count);
    int offset = 6 + 16 * pngs.Count;
    foreach (var (size, png) in pngs)
    {
        w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
        w.Write(png.Length); w.Write(offset);
        offset += png.Length;
    }
    foreach (var (_, png) in pngs) w.Write(png);
}
File.WriteAllBytes(Path.ChangeExtension(output, ".png"), Render(256));
Console.WriteLine($"wrote {output} ({string.Join(",", sizes)})");

static byte[] Render(int size)
{
    using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp))
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        float k = size / 256f;
        g.ScaleTransform(k, k);
        bool small = size <= 24;

        // tile: Instrument's inverted pill colour, small radius like the theme's panels
        float inset = small ? 4 : 12;
        using (var tile = RoundedRect(inset, inset, 256 - 2 * inset, 256 - 2 * inset, small ? 40 : 48))
        using (var ink = new SolidBrush(Color.FromArgb(0x1B, 0x1B, 0x1B)))
            g.FillPath(ink, tile);

        // h: stem, shoulder arc and right leg; thicker strokes at tray sizes
        float w = small ? 38 : 32;
        using (var light = new SolidBrush(Color.FromArgb(0xEB, 0xE8, 0xE1)))
        using (var arch = new Pen(Color.FromArgb(0xEB, 0xE8, 0xE1), w))
        {
            const float cx = 102, cy = 124, r = 40;
            g.FillRectangle(light, cx - r - w / 2, 46, w, 162);           // stem
            g.FillRectangle(light, cx + r - w / 2, cy - 2, w, 210 - cy);   // right leg (overlaps the arc: no seam)
            g.DrawArc(arch, cx - r, cy - r, 2 * r, 2 * r, 180, 180);      // shoulder
        }

        // the accent dot, square like the theme's bars
        float dot = small ? 40 : 34;
        using (var accent = new SolidBrush(Color.FromArgb(0xFF, 0x5A, 0x1F)))
            g.FillRectangle(accent, 176, 208 - dot, dot, dot);
    }
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    return ms.ToArray();
}

static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
{
    var p = new GraphicsPath();
    float d = r * 2;
    p.AddArc(x, y, d, d, 180, 90);
    p.AddArc(x + w - d, y, d, d, 270, 90);
    p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
    p.AddArc(x, y + h - d, d, d, 90, 90);
    p.CloseFigure();
    return p;
}
