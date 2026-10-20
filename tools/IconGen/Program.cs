using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// Renders the Hub logo (same geometry as assets/hub-logo.svg, 256-unit canvas) into a multi-size .ico.
// Small sizes drop the orbit ring and thicken strokes so the mark stays legible in the tray.
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

        float inset = small ? 2 : 8, radius = small ? 64 : 60;
        using (var tile = RoundedRect(inset, inset, 256 - 2 * inset, 256 - 2 * inset, radius))
        using (var grad = new LinearGradientBrush(new PointF(0, 0), new PointF(256, 256), Color.FromArgb(0x6D, 0x5B, 0xFF), Color.FromArgb(0x1E, 0xC8, 0xE8)))
            g.FillPath(grad, tile);

        var center = new PointF(128, 132);
        PointF[] nodes = small
            ? [new(128, 52), new(197, 172), new(59, 172)]
            : [new(128, 62), new(188.6f, 167), new(67.4f, 167)];

        if (!small)
            using (var ring = new Pen(Color.FromArgb(72, 255, 255, 255), 10))
                g.DrawEllipse(ring, center.X - 70, center.Y - 70, 140, 140);

        using (var spoke = new Pen(Color.White, small ? 26 : 16) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            foreach (var n in nodes) g.DrawLine(spoke, center, n);

        float nodeR = small ? 32 : 24, hubR = small ? 42 : 32;
        foreach (var n in nodes) g.FillEllipse(Brushes.White, n.X - nodeR, n.Y - nodeR, nodeR * 2, nodeR * 2);
        g.FillEllipse(Brushes.White, center.X - hubR, center.Y - hubR, hubR * 2, hubR * 2);
        if (!small)
            using (var core = new SolidBrush(Color.FromArgb(0x5B, 0x6C, 0xF5)))
                g.FillEllipse(core, center.X - 13, center.Y - 13, 26, 26);
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
