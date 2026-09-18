using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Builds the application icon at every size Windows asks for, and writes a
// multi-image .ico.
//
// The mark: an envelope with its left half cut away, the missing half drawn
// back as a letter E in a second colour. One stroke weight throughout — the
// colour is the only thing separating the letter from the envelope.
//
// Drawn with WPF primitives rather than parsing the SVG, because the geometry
// is six straight paths and a parser would be the larger dependency. The SVG
// in src/Mail.App/Assets/icon.svg is the reference; these coordinates match it.

const double L = 16, R = 88, T = 26, B = 78;   // the FULL envelope's bounds
const double MidX = (L + R) / 2;               // flap point: its horizontal centre
var MidY = T + (B - T) * 0.52;

var ink = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));
var blue = new SolidColorBrush(Color.FromRgb(0x4F, 0xA3, 0xE3));
ink.Freeze();
blue.Freeze();

// Windows uses 16 in title bars and Alt-Tab, 32 on the taskbar, 256 for the
// large icon view; the rest fill in between so it never has to scale one.
int[] sizes = [16, 20, 24, 32, 48, 64, 128, 256];

var repoRoot = FindRepoRoot();
var output = Path.Combine(repoRoot, "src", "Mail.App", "Assets", "icon.ico");
Directory.CreateDirectory(Path.GetDirectoryName(output)!);

var frames = sizes.Select(Render).ToList();
using (var stream = File.Create(output))
    WriteIco(stream, frames);

Console.WriteLine($"Wrote {output}");
foreach (var (size, bytes) in sizes.Zip(frames))
    Console.WriteLine($"  {size,3}px  {bytes.Length,7:N0} bytes");

// Renders the mark at one size. Stroke thickness scales with the canvas, so
// the proportions hold; below 32px the line is widened slightly because a
// sub-pixel stroke greys out into near-invisibility.
byte[] Render(int size)
{
    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
    {
        var scale = size / 100.0;
        var weight = size <= 24 ? 9.5 : size <= 32 ? 8.8 : 8.0;
        var pen = new Pen(ink, weight * scale)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        var accent = new Pen(blue, weight * scale)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        accent.Freeze();

        Point P(double x, double y) => new(x * scale, y * scale);

        // Envelope: top edge, right edge, bottom edge. No left edge exists —
        // that is the half that was cut away.
        var envelope = new PathGeometry();
        var body = new PathFigure { StartPoint = P(L, T) };
        body.Segments.Add(new LineSegment(P(R, T), true));
        body.Segments.Add(new LineSegment(P(R, B), true));
        body.Segments.Add(new LineSegment(P(L, B), true));
        envelope.Figures.Add(body);
        dc.DrawGeometry(null, pen, envelope);

        // The flap's surviving diagonal, ending at the full envelope's centre.
        dc.DrawLine(pen, P(R, T), P(MidX, MidY));

        // The E: the same line continuing left, spine standing where the
        // envelope's left edge used to be.
        dc.DrawLine(accent, P(L, T), P(L, B));
        dc.DrawLine(accent, P(L, T), P(MidX, T));
        dc.DrawLine(accent, P(L, MidY), P(MidX, MidY));
        dc.DrawLine(accent, P(L, B), P(MidX, B));
    }

    var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(visual);

    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var buffer = new MemoryStream();
    encoder.Save(buffer);
    return buffer.ToArray();
}

// Writes an ICO whose images are PNG-compressed. Vista and later read PNG
// frames directly, which keeps a 256px icon from costing 256KB as a bitmap.
static void WriteIco(Stream stream, IReadOnlyList<byte[]> frames)
{
    using var w = new BinaryWriter(stream);
    w.Write((ushort)0);               // reserved
    w.Write((ushort)1);               // type: icon
    w.Write((ushort)frames.Count);

    // Directory entries come first, so the offset of each image is the header
    // plus every entry plus the images already placed.
    var offset = 6 + frames.Count * 16;
    var sizes = new[] { 16, 20, 24, 32, 48, 64, 128, 256 };
    for (var i = 0; i < frames.Count; i++)
    {
        var size = sizes[i];
        w.Write((byte)(size >= 256 ? 0 : size));   // 0 means 256
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0);             // palette colours: none, it is true colour
        w.Write((byte)0);             // reserved
        w.Write((ushort)1);           // colour planes
        w.Write((ushort)32);          // bits per pixel
        w.Write(frames[i].Length);
        w.Write(offset);
        offset += frames[i].Length;
    }
    foreach (var frame in frames)
        w.Write(frame);
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
        dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("Not inside the repository.");
}
