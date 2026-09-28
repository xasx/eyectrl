# App Icon Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace `src/EyeCtrl.App/Assets/eyectrl.ico` with the approved six-frame icon (dark Midnight tile, cyan almond, amber ¾-pie iris) built by a deterministic WPF rasterizer tool, and make the tray actually request the tuned small frame.

**Architecture:** A standalone console tool (`tools/EyeCtrl.IconBuilder`, not in the solution) renders the spec's 96-grid geometry via `DrawingVisual` + `RenderTargetBitmap` in software mode and packs a standard ICO (BMP frames 16–128, PNG 256). The asset is swapped, a headless acceptance harness in `EyeCtrl.App.Tests` pins the artifact (A1–A4), and `TrayIconManager` gets the L10 one-liner refactored into a testable static (A4b). A5/A6 are owner eyeball gates.

**Tech Stack:** .NET 10 WPF (`RenderTargetBitmap`, `DrawingVisual`, `BlurEffect`, `PngBitmapEncoder`), `System.Drawing.Icon` (ICO decode + pixel probes), xunit.v3 + NFluent + `Xunit.StaFact`, python (stdlib `struct`) for hex-level verification steps.

**Spec:** `docs/superpowers/specs/2026-09-28-app-icon-redesign-design.md` (@ 9af69129) — the executor should have it open; every constant below is copied from it.

## Global Constraints

- All work happens in the worktree `C:\Users\andre\source\repos\eyectrl\.worktrees\app-icon-redesign`, branch `feat/app-icon-redesign`. Commit there; **never push** without the owner asking.
- The builder project is **not added to `EyeCtrl.slnx`** (spec L8/L12; precedent `tools/CorpusBuilder`).
- ICO layout: 6 directory entries **ascending** 16/32/48/64/128/256; BMP payloads (32 bpp, `BI_RGB`, `biHeight = 2·h`, rows bottom-up) for 16–128; **PNG** for the 256 entry (width = height = 0 in the directory); all entries planes = 1, bit count = 32, colour count = 0; AND-mask rows bottom-up, padded to 4 bytes, bit set where α == 0.
- Pixel pipeline: render `Pbgra32` → convert to **Bgra32 straight alpha** (un-premultiply) before encoding.
- Blur: `BlurEffect` **Radius = 12** (96-grid units; r2 measured σ ≈ Radius/3), glow in its own visual (an Effect applies to the visual's whole subtree).
- Palette (from the spec's colour table): tile gradient `#252B4A`→`#141828` (centre 50 %,38 %, r 0.85), flat 16 px tile `#1A2038`, rim `#4A5578` @ 55 %, glow `#A5F3FC` @ 10 %, body `#AFF0FF`→`#4A7ECF` (y 25→71), iris track `#141828` + hairline `#0A0A14`, whisper arc `#A5F3FC` (master α 0.55 / 32 px α 0.60), pie gradient `#F6C77E`→`#E8A33C` (StartPoint (60,48), EndPoint (36,44)), 16 px pie solid `#E8A33C`, dot `#F7FCFF`.
- Frame plans (spec layers × frames table): master (48/64/128/256) = rim + glow + hairline + arc r 11.5 w 2 + pie r 12.5 gradient + dot (60.5,48) r 4.5; 32 px = no rim/glow/hairline, iris r 14, arc r 11.5 **w 3 α 0.60**, pie r 13 gradient, dot (61,48) r 5; 16 px = flat tile, iris r 15.5, **no arc/rim/glow/hairline**, pie r 14.5 **solid**, dot (62.5,48) r 6.
- L10 code change shape: extract `internal static Icon LoadTrayIcon(Stream, System.Drawing.Size)`; the ctor passes `SystemInformation.SmallIconSize`. `TrayIconManager.cs:85` must never be bare `Icon(Stream)` again.
- Tests use explicit `System.Drawing.Size` only (SmallIconSize is 32 @ 192 DPI but 16 @ 96 DPI on CI). Tests never call bare `new Icon(stream)`.
- `EYECTRL_UI_TESTS` is **never set** in any step; FlaUI app-launching tests are off-limits (they seize the owner's cursor — AppFixture.cs:41-51).
- The app is **never launched** by any step. A6's visual check belongs to the owner, who launches when they choose.
- Every task ends with a commit on `feat/app-icon-redesign`.

## File Structure

| File | Responsibility |
|---|---|
| `tools/EyeCtrl.IconBuilder/EyeCtrl.IconBuilder.csproj` | console exe, `net10.0-windows`, `UseWPF`, STA entry |
| `tools/EyeCtrl.IconBuilder/IconSpec.cs` | all spec constants (colours, geometry strings, per-frame plans) — the only place spec values live |
| `tools/EyeCtrl.IconBuilder/FrameRenderer.cs` | renders one `FramePlan` to straight-BGRA pixels; PNG encode; BitmapSource helper |
| `tools/EyeCtrl.IconBuilder/IcoWriter.cs` | BMP encoder + ICO container assembly (deterministic) |
| `tools/EyeCtrl.IconBuilder/PreviewWriter.cs` | `--preview` dark/light strips at the real per-surface sizes |
| `tools/EyeCtrl.IconBuilder/Program.cs` | arg parsing, repo-root discovery, orchestration |
| `src/EyeCtrl.App/Assets/eyectrl.ico` | the regenerated artifact (binary, swapped in Task 4) |
| `tests/EyeCtrl.App.Tests/Assets/AppIconTests.cs` | A2 (ICONDIR parse), A3 (splash pin), A4a (explicit-size frame + pixel probes) |
| `tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj` | add `UseWindowsForms` (System.Drawing for Icon probes) |
| `src/EyeCtrl.App/Services/TrayIconManager.cs` | L10 refactor: extract `LoadTrayIcon`, ctor passes `SmallIconSize` |

---

### Task 1: Builder project + master-frame renderer (PNG verification)

**Files:**
- Create: `tools/EyeCtrl.IconBuilder/EyeCtrl.IconBuilder.csproj`
- Create: `tools/EyeCtrl.IconBuilder/IconSpec.cs`
- Create: `tools/EyeCtrl.IconBuilder/FrameRenderer.cs`
- Create: `tools/EyeCtrl.IconBuilder/Program.cs`

**Interfaces:**
- Consumes: nothing (first task).
- Produces: `IconSpec.All` (`IReadOnlyList<FramePlan>`, ascending 16→256), `FramePlan` record, `FramePlan.Master(int size)`, `FramePlan.PlanFor(int size)`, `FrameRenderer.RenderBgra(FramePlan): byte[]` (straight BGRA, top-down), `FrameRenderer.EncodePng(int size, byte[] bgra): byte[]`, `Program.FindRepoRoot(string): string`. Task 2 consumes `RenderBgra` + `EncodePng`; Task 3 consumes `PlanFor`; Task 4 consumes the assembled program.

- [ ] **Step 1: Create the csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>EyeCtrl.IconBuilder</RootNamespace>
    <AssemblyName>EyeCtrl.IconBuilder</AssemblyName>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Create `IconSpec.cs`** — every value from the spec's geometry + colour tables:

```csharp
namespace EyeCtrl.IconBuilder;

using System.Windows;
using System.Windows.Media;

/// <summary>
/// All constants come from docs/superpowers/specs/2026-09-28-app-icon-redesign-design.md
/// (@ 9af69129). The 96-grid design renders at scale size/96.
/// </summary>
internal static class IconSpec
{
    public const double GridSize = 96;

    // Tile
    public const double TileX = 4, TileY = 4, TileExtent = 88, TileRadius = 22;
    public static readonly Color TileLight = Color.FromRgb(0x25, 0x2B, 0x4A);
    public static readonly Color TileDark = Color.FromRgb(0x14, 0x18, 0x28);
    public static readonly Color TileFlat16 = Color.FromRgb(0x1A, 0x20, 0x38);
    public static readonly Color Rim = Color.FromArgb(140, 0x4A, 0x55, 0x78); // 55 %

    // Glow (own visual — an Effect applies to the visual's whole subtree)
    public static readonly Point GlowCenter = new(48, 47);
    public const double GlowRx = 30, GlowRy = 21;
    public static readonly Color Glow = Color.FromArgb(26, 0xA5, 0xF3, 0xFC); // 10 %
    public const double BlurRadius = 12; // r2 measured sigma ≈ Radius/3, local (96-grid) units

    // Body (almond)
    public const string BodyPath = "M12,48 C26,26 70,26 84,48 C70,70 26,70 12,48 Z";
    public static readonly Color BodyTop = Color.FromRgb(0xAF, 0xF0, 0xFF);
    public static readonly Color BodyBottom = Color.FromRgb(0x4A, 0x7E, 0xCF);
    public const double BodyGradientTop = 25, BodyGradientBottom = 71;

    // Iris track + hairline
    public static readonly Color Track = Color.FromRgb(0x14, 0x18, 0x28);
    public static readonly Color Hairline = Color.FromRgb(0x0A, 0x0A, 0x14);

    // Whisper arc
    public static readonly Color Arc = Color.FromRgb(0xA5, 0xF3, 0xFC);

    // Pie (¾ CCW from 12 o'clock to 3 o'clock — deliberate mirror of DwellArcButton's CW sweep)
    public static readonly Color PieHead = Color.FromRgb(0xF6, 0xC7, 0x7E);   // at (60,48)
    public static readonly Color PieTail = Color.FromRgb(0xE8, 0xA3, 0x3C);   // at (36,44)
    public static readonly Color PieSolid16 = Color.FromRgb(0xE8, 0xA3, 0x3C);

    // Gaze dot
    public static readonly Color Dot = Color.FromRgb(0xF7, 0xFC, 0xFF);

    public static string PiePath(double radius) =>
        $"M48,48 L48,{48 - radius} A{radius},{radius} 0 1 0 {48 + radius},48 Z";

    public static string ArcPath(double radius) =>
        $"M{48 + radius},48 A{radius},{radius} 0 0 0 48,{48 - radius}";
}

/// <summary>Per-frame layer toggles — the spec's layers × frames table, one record per frame.</summary>
internal sealed record FramePlan(
    int Size,
    bool FlatTile,
    bool Rim,
    bool Glow,
    bool Hairline,
    double TrackRadius,
    double? ArcRadius,
    double? ArcWidth,
    double ArcAlpha,        // 0..1 brush opacity
    double PieRadius,
    bool PieGradient,       // false = solid PieSolid16
    Point DotCenter,
    double DotRadius)
{
    /// <summary>The six ICO frames, ascending (directory order).</summary>
    public static IReadOnlyList<FramePlan> All { get; } =
    [
        Tuned16,
        Tuned32,
        Master(48),
        Master(64),
        Master(128),
        Master(256),
    ];

    public static FramePlan PlanFor(int size) =>
        All.FirstOrDefault(p => p.Size == size) ?? Master(size);

    public static FramePlan Master(int size) => new(
        size,
        FlatTile: false, Rim: true, Glow: true, Hairline: true,
        TrackRadius: 13.5,
        ArcRadius: 11.5, ArcWidth: 2, ArcAlpha: 0.55,
        PieRadius: 12.5, PieGradient: true,
        DotCenter: new Point(60.5, 48), DotRadius: 4.5);

    private static FramePlan Tuned32 { get; } = new(
        32,
        FlatTile: false, Rim: false, Glow: false, Hairline: false,
        TrackRadius: 14,
        ArcRadius: 11.5, ArcWidth: 3, ArcAlpha: 0.60,
        PieRadius: 13, PieGradient: true,
        DotCenter: new Point(61, 48), DotRadius: 5);

    private static FramePlan Tuned16 { get; } = new(
        16,
        FlatTile: true, Rim: false, Glow: false, Hairline: false,
        TrackRadius: 15.5,
        ArcRadius: null, ArcWidth: null, ArcAlpha: 0,
        PieRadius: 14.5, PieGradient: false,
        DotCenter: new Point(62.5, 48), DotRadius: 6);
}
```

- [ ] **Step 3: Create `FrameRenderer.cs`**

```csharp
namespace EyeCtrl.IconBuilder;

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

internal static class FrameRenderer
{
    /// <summary>Renders one frame to straight-alpha BGRA pixels, top-down rows.</summary>
    public static byte[] RenderBgra(FramePlan plan)
    {
        var scale = plan.Size / IconSpec.GridSize;

        var main = new DrawingVisual();
        using (var dc = main.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            DrawTile(dc, plan);
            DrawBody(dc);
            DrawTrack(dc, plan);
            if (plan.ArcRadius is double arcRadius)
                DrawArc(dc, plan, arcRadius, plan.ArcWidth!.Value);
            DrawPie(dc, plan);
            DrawDot(dc, plan);
            dc.Pop();
        }

        var rtb = new RenderTargetBitmap(plan.Size, plan.Size, 96, 96, PixelFormats.Pbgra32);

        // The glow goes first, in its own visual: a BlurEffect on the main visual would blur
        // every layer (an Effect applies to the visual's whole subtree).
        if (plan.Glow)
        {
            var glow = new DrawingVisual();
            glow.Effect = new BlurEffect { Radius = IconSpec.BlurRadius };
            using (var dc = glow.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(scale, scale));
                dc.DrawEllipse(new SolidColorBrush(IconSpec.Glow), null,
                    IconSpec.GlowCenter, IconSpec.GlowRx, IconSpec.GlowRy);
                dc.Pop();
            }
            rtb.Render(glow);
        }
        rtb.Render(main);

        return ToStraightBgra(rtb);
    }

    public static BitmapSource ToBitmapSource(int size, byte[] bgra) =>
        BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, bgra, size * 4);

    public static byte[] EncodePng(int size, byte[] bgra)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(ToBitmapSource(size, bgra)));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static void DrawTile(DrawingContext dc, FramePlan plan)
    {
        var rect = new Rect(IconSpec.TileX, IconSpec.TileY, IconSpec.TileExtent, IconSpec.TileExtent);

        Brush fill;
        if (plan.FlatTile)
        {
            fill = new SolidColorBrush(IconSpec.TileFlat16);
        }
        else
        {
            // SVG radial (cx,cy,r) → WPF: Center = GradientOrigin = (0.5,0.38), RadiusX = RadiusY = 0.85.
            var brush = new RadialGradientBrush(IconSpec.TileLight, IconSpec.TileDark)
            {
                Center = new Point(0.5, 0.38),
                GradientOrigin = new Point(0.5, 0.38),
                RadiusX = 0.85,
                RadiusY = 0.85,
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
            };
            fill = brush;
        }
        dc.DrawRoundedRectangle(fill, null, rect, IconSpec.TileRadius, IconSpec.TileRadius);

        if (plan.Rim)
        {
            var rimRect = new Rect(IconSpec.TileX + 1.5, IconSpec.TileY + 1.5,
                IconSpec.TileExtent - 3, IconSpec.TileExtent - 3);
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(IconSpec.Rim), 2),
                rimRect, IconSpec.TileRadius - 1.5, IconSpec.TileRadius - 1.5);
        }
    }

    private static void DrawBody(DrawingContext dc)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(48, IconSpec.BodyGradientTop),
            EndPoint = new Point(48, IconSpec.BodyGradientBottom),
            MappingMode = BrushMappingMode.Absolute,
            GradientStops =
            {
                new GradientStop(IconSpec.BodyTop, 0),
                new GradientStop(IconSpec.BodyBottom, 1),
            },
        };
        dc.DrawGeometry(brush, null, Geometry.Parse(IconSpec.BodyPath));
    }

    private static void DrawTrack(DrawingContext dc, FramePlan plan)
    {
        dc.DrawEllipse(new SolidColorBrush(IconSpec.Track),
            plan.Hairline ? new Pen(new SolidColorBrush(IconSpec.Hairline), 1) : null,
            new Point(48, 48), plan.TrackRadius, plan.TrackRadius);
    }

    private static void DrawArc(DrawingContext dc, FramePlan plan, double radius, double width)
    {
        var brush = new SolidColorBrush(IconSpec.Arc) { Opacity = plan.ArcAlpha };
        var pen = new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, pen, Geometry.Parse(IconSpec.ArcPath(radius)));
    }

    private static void DrawPie(DrawingContext dc, FramePlan plan)
    {
        Brush brush;
        if (plan.PieGradient)
        {
            brush = new LinearGradientBrush
            {
                StartPoint = new Point(60, 48),   // PieHead
                EndPoint = new Point(36, 44),     // PieTail
                MappingMode = BrushMappingMode.Absolute,
                GradientStops =
                {
                    new GradientStop(IconSpec.PieHead, 0),
                    new GradientStop(IconSpec.PieTail, 1),
                },
            };
        }
        else
        {
            brush = new SolidColorBrush(IconSpec.PieSolid16);
        }
        dc.DrawGeometry(brush, null, Geometry.Parse(IconSpec.PiePath(plan.PieRadius)));
    }

    private static void DrawDot(DrawingContext dc, FramePlan plan)
    {
        dc.DrawEllipse(new SolidColorBrush(IconSpec.Dot), null, plan.DotCenter, plan.DotRadius, plan.DotRadius);
    }

    private static byte[] ToStraightBgra(RenderTargetBitmap rtb)
    {
        int w = rtb.PixelWidth, h = rtb.PixelHeight;
        var pbgra = new byte[w * h * 4];
        rtb.CopyPixels(pbgra, w * 4, 0);

        var bgra = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            byte b = pbgra[i * 4], g = pbgra[i * 4 + 1], r = pbgra[i * 4 + 2], a = pbgra[i * 4 + 3];
            if (a > 0)
            {
                // Pbgra32 is premultiplied; straighten before encoding or AA edges get a dark fringe.
                r = (byte)Math.Min(255, r * 255 / a);
                g = (byte)Math.Min(255, g * 255 / a);
                b = (byte)Math.Min(255, b * 255 / a);
            }
            bgra[i * 4] = b;
            bgra[i * 4 + 1] = g;
            bgra[i * 4 + 2] = r;
            bgra[i * 4 + 3] = a;
        }
        return bgra;
    }
}
```

- [ ] **Step 4: Create `Program.cs`** (Task 1 version — `--png-dir` only; `--out`/`--preview` arrive with Tasks 2/3):

```csharp
namespace EyeCtrl.IconBuilder;

using System.IO;
using System.Windows;
using System.Windows.Media;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // A1 determinism: rasterize in software so no GPU/driver variance can leak into bytes.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        string? pngDir = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--png-dir" && i + 1 < args.Length)
                pngDir = args[++i];
        }
        if (pngDir is null)
        {
            Console.WriteLine("nothing to do: pass --png-dir <dir> to render the six frames as PNGs");
            return 1;
        }

        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        Directory.CreateDirectory(pngDir);

        foreach (var plan in FramePlan.All)
        {
            var bgra = FrameRenderer.RenderBgra(plan);
            var png = FrameRenderer.EncodePng(plan.Size, bgra);
            var path = Path.Combine(pngDir!, $"{plan.Size}.png");
            File.WriteAllBytes(path, png);
            Console.WriteLine($"wrote {path} ({png.Length} bytes)");
        }
        return 0;
    }

    /// <summary>Walks up from <paramref name="start"/> to the directory holding EyeCtrl.slnx.</summary>
    public static string FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EyeCtrl.slnx")))
            dir = dir.Parent!;
        if (dir is null)
            throw new InvalidOperationException("EyeCtrl.slnx not found above " + start);
        return dir.FullName;
    }
}
```

- [ ] **Step 5: Run and verify the master render**

Run: `dotnet run --project tools/EyeCtrl.IconBuilder -- --png-dir "$TEMP/iconbuild/task1"`
Expected: six lines `wrote ...png`. Then **Read** `$TEMP/iconbuild/task1/256.png` and `48.png` as images and verify against the approved design: dark rounded tile, cyan almond, dark iris ring, amber ¾ pie with the gap at the upper right, white dot on the pie's right edge, thin cyan whisper arc in the gap, soft cyan glow. If the glow at 48 px looks proportionally larger than at 256 px (device-space blur), change `Radius = IconSpec.BlurRadius` to `Radius = IconSpec.BlurRadius * scale` in `FrameRenderer` and regenerate — the r2 measurement implies 96-grid-local units, so proportional glow is the expected outcome, but this check settles it empirically.

- [ ] **Step 6: Commit**

```bash
git add tools/EyeCtrl.IconBuilder
git commit -m "feat(icon-builder): WPF frame rasterizer — spec constants, per-frame plans, master render"
```

---

### Task 2: ICO container writer (BMP frames + 256 PNG)

**Files:**
- Create: `tools/EyeCtrl.IconBuilder/IcoWriter.cs`
- Modify: `tools/EyeCtrl.IconBuilder/Program.cs` (add `--out`)

**Interfaces:**
- Consumes: `FramePlan.All`, `FrameRenderer.RenderBgra`, `FrameRenderer.EncodePng` (Task 1).
- Produces: `IcoWriter.Build(IReadOnlyList<FramePlan>): byte[]` — the complete ICO file. Task 4's asset swap and Task 5's A2 assertions depend on this byte layout.

- [ ] **Step 1: Create `IcoWriter.cs`**

```csharp
namespace EyeCtrl.IconBuilder;

using System.IO;

internal static class IcoWriter
{
    /// <summary>
    /// Packs the ascending frames into an ICO: BMP payloads (32bpp BI_RGB, biHeight = 2·h,
    /// rows bottom-up, AND mask bottom-up padded to 4 bytes with bits set where α == 0)
    /// for sizes below 256, PNG for the 256 entry (directory width/height = 0).
    /// All directory entries: planes = 1, bit count = 32, colour count = 0.
    /// </summary>
    public static byte[] Build(IReadOnlyList<FramePlan> plans)
    {
        var payloads = new List<byte[]>();
        foreach (var plan in plans)
        {
            var bgra = FrameRenderer.RenderBgra(plan);
            payloads.Add(plan.Size == 256
                ? FrameRenderer.EncodePng(plan.Size, bgra)
                : EncodeBmp(plan.Size, bgra));
        }

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((short)0);                       // reserved
        bw.Write((short)1);                       // type: icon
        bw.Write((short)payloads.Count);

        int offset = 6 + 16 * payloads.Count;
        for (int i = 0; i < plans.Count; i++)
        {
            int size = plans[i].Size;
            byte dimension = size == 256 ? (byte)0 : (byte)size;
            bw.Write(dimension);                  // width
            bw.Write(dimension);                  // height
            bw.Write((byte)0);                    // colour count
            bw.Write((byte)0);                    // reserved
            bw.Write((short)1);                   // planes
            bw.Write((short)32);                  // bit count
            bw.Write(payloads[i].Length);
            bw.Write(offset);
            offset += payloads[i].Length;
        }
        foreach (var payload in payloads)
            bw.Write(payload);

        bw.Flush();
        return ms.ToArray();
    }

    private static byte[] EncodeBmp(int size, byte[] bgraTopDown)
    {
        int andStride = (size + 31) / 32 * 4;     // 1bpp rows padded to 4 bytes
        int andSize = andStride * size;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(40);                             // biSize
        bw.Write(size);                           // biWidth
        bw.Write(size * 2);                       // biHeight = XOR + AND
        bw.Write((short)1);                       // biPlanes
        bw.Write((short)32);                      // biBitCount
        bw.Write(0);                              // biCompression = BI_RGB
        bw.Write(size * 4 * size + andSize);      // biSizeImage
        bw.Write(0);                              // biXPelsPerMeter
        bw.Write(0);                              // biYPelsPerMeter
        bw.Write(0);                              // biClrUsed
        bw.Write(0);                              // biClrImportant

        for (int row = size - 1; row >= 0; row--) // XOR rows, bottom-up
            bw.Write(bgraTopDown.AsSpan(row * size * 4, size * 4).ToArray());

        for (int row = size - 1; row >= 0; row--) // AND rows, bottom-up, MSB-first
        {
            var rowBytes = new byte[andStride];
            for (int x = 0; x < size; x++)
                if (bgraTopDown[(row * size + x) * 4 + 3] == 0)
                    rowBytes[x / 8] |= (byte)(0x80 >> (x % 8));
            bw.Write(rowBytes);
        }

        bw.Flush();
        return ms.ToArray();
    }
}
```

- [ ] **Step 2: Extend `Program.cs`** — add `--out` handling. Replace the body after the arg loop with:

```csharp
        string? outPath = null;
        string? pngDir = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length)
                outPath = args[++i];
            else if (args[i] == "--png-dir" && i + 1 < args.Length)
                pngDir = args[++i];
        }

        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        outPath ??= Path.Combine(repoRoot, "src", "EyeCtrl.App", "Assets", "eyectrl.ico");

        var ico = IcoWriter.Build(FramePlan.All);
        File.WriteAllBytes(outPath, ico);
        Console.WriteLine($"wrote {outPath} ({ico.Length} bytes)");
```

(Keep `FindRepoRoot` and the `[STAThread]`/`RenderOptions` lines from Task 1. Delete the `--png-dir` block from Task 1 — Task 3's `--preview` replaces it.)

- [ ] **Step 3: Run to a temp path and verify the container with python**

Write this checker to a file first (heredocs get mangled by the shell transport):

`%TEMP%/check_ico.py`:

```python
import struct, sys

data = open(sys.argv[1], "rb").read()
reserved, kind, count = struct.unpack_from("<HHH", data, 0)
print("reserved", reserved, "type", kind, "count", count)
assert reserved == 0 and kind == 1 and count == 6

sizes = []
offset = 6
for i in range(count):
    w, h, cc, rsv, planes, bc, size, img_off = struct.unpack_from("<BBBBHHII", data, offset)
    offset += 16
    shown = 256 if w == 0 else w
    payload = data[img_off:img_off + size]
    if payload[:4] == b"\x89PNG":
        line = f"{shown} PNG"
    else:
        bi_size, bi_w, bi_h, bi_planes, bi_bpp, bi_comp = struct.unpack_from("<IiiHHI", payload, 0)
        assert bi_size == 40 and bi_bpp == 32 and bi_comp == 0, (bi_size, bi_bpp, bi_comp)
        assert bi_h == shown * 2, bi_h
        line = f"{shown} BMP biHeight={bi_h} bpp={bi_bpp} comp={bi_comp}"
    print(line, "planes", planes, "bc", bc, "cc", cc, "rsv", rsv, "w==h" if w == h else "W!=H!")
    sizes.append(shown)

assert sizes == [16, 32, 48, 64, 128, 256], sizes
print("OK: 6 frames ascending")
```

Run: `dotnet run --project tools/EyeCtrl.IconBuilder -- --out "$TEMP/iconbuild/task2.ico"` then `python "$TEMP/check_ico.py" "$TEMP/iconbuild/task2.ico"`

Expected output:

```
reserved 0 type 1 count 6
16 BMP biHeight=32 bpp=32 comp=0 planes 1 bc 32 cc 0 rsv 0 w==h
32 BMP biHeight=64 bpp=32 comp=0 planes 1 bc 32 cc 0 rsv 0 w==h
48 BMP biHeight=96 bpp=32 comp=0 planes 1 bc 32 cc 0 rsv 0 w==h
64 BMP biHeight=128 bpp=32 comp=0 planes 1 bc 32 cc 0 rsv 0 w==h
128 BMP biHeight=256 bpp=32 comp=0 planes 1 bc 32 cc 0 rsv 0 w==h
256 PNG planes 1 bc 32 cc 0 rsv 0 w==h
OK: 6 frames ascending
```

- [ ] **Step 4: Commit**

```bash
git add tools/EyeCtrl.IconBuilder
git commit -m "feat(icon-builder): ICO container — BMP frames (biHeight=2h, AND mask) + PNG 256, ascending directory"
```

---

### Task 3: `--preview` mode (A5 producer)

**Files:**
- Create: `tools/EyeCtrl.IconBuilder/PreviewWriter.cs`
- Modify: `tools/EyeCtrl.IconBuilder/Program.cs` (add `--preview <dir>`)

**Interfaces:**
- Consumes: `FramePlan.PlanFor`, `FrameRenderer.RenderBgra`/`ToBitmapSource` (Task 1).
- Produces: `PreviewWriter.Write(string dir)` — writes `preview-dark.png` and `preview-light.png`. A5's owner eyeball reads these.

- [ ] **Step 1: Create `PreviewWriter.cs`**

```csharp
namespace EyeCtrl.IconBuilder;

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class PreviewWriter
{
    // A5 surfaces: 16 (tray floor), 32 (tray @200% + Explorer small), 48/64 (pinned shortcut),
    // 96 (Explorer medium), 128 (splash @64 DIP). Each rendered with the frame plan that
    // surface actually consumes, on dark and light taskbar-like chips.
    private static readonly int[] Sizes = [16, 32, 48, 64, 96, 128];

    public static void Write(string dir)
    {
        Directory.CreateDirectory(dir);
        WriteStrip(Path.Combine(dir, "preview-dark.png"), Color.FromRgb(0x1F, 0x1F, 0x1F));
        WriteStrip(Path.Combine(dir, "preview-light.png"), Color.FromRgb(0xF4, 0xF4, 0xF4));
    }

    private static void WriteStrip(string path, Color chip)
    {
        const int pad = 10, gap = 14;
        int tallest = Sizes.Max();
        int width = pad * 2 + Sizes.Sum() + gap * (Sizes.Length - 1);
        int height = pad * 2 + tallest;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(chip), null, new Rect(0, 0, width, height));
            int x = pad;
            foreach (var size in Sizes)
            {
                var bgra = FrameRenderer.RenderBgra(FramePlan.PlanFor(size));
                var image = FrameRenderer.ToBitmapSource(size, bgra);
                dc.DrawImage(image, new Rect(x, pad + (tallest - size), size, size));
                x += size + gap;
            }
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        using var fs = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        encoder.Save(fs);
        Console.WriteLine($"wrote {path}");
    }
}
```

- [ ] **Step 2: Add `--preview <dir>` to `Program.cs`** — extend the arg loop with a third branch (`else if (args[i] == "--preview" && i + 1 < args.Length) previewDir = args[++i];`, declare `string? previewDir = null;` alongside the others) and after the `--out` write:

```csharp
        if (previewDir is not null)
            PreviewWriter.Write(previewDir);
```

- [ ] **Step 3: Generate and inspect the strips**

Run: `dotnet run --project tools/EyeCtrl.IconBuilder -- --out "$TEMP/iconbuild/task3.ico" --preview "$TEMP/iconbuild/preview"`

**Read** `$TEMP/iconbuild/preview/preview-dark.png` and `preview-light.png` as images. Expected: six icons per strip (16/32/48/64/96/128, ascending left→right, bottom-aligned); the 16 and 32 frames read as tile + eye + amber iris + dot; nothing muddy; the tiles hold their own on both chips.

- [ ] **Step 4: Owner A5 eyeball**

Show the two strips to the owner (file paths are enough — they view with their eyes; do **not** launch anything). A5 passes when the owner confirms the small frames read correctly. If the owner flags a frame, fix the `FramePlan` values per the spec and regenerate before committing.

- [ ] **Step 5: Commit**

```bash
git add tools/EyeCtrl.IconBuilder
git commit -m "feat(icon-builder): --preview strips at real per-surface sizes (A5)"
```

---

### Task 4: Swap the asset (A1 determinism)

**Files:**
- Modify: `src/EyeCtrl.App/Assets/eyectrl.ico` (binary swap)

**Interfaces:**
- Consumes: the completed builder (Tasks 1–3).
- Produces: the new committed artifact that Tasks 5–6's tests assert against.

- [ ] **Step 1: Generate twice and compare (A1)**

Run: `dotnet run --project tools/EyeCtrl.IconBuilder -- --out "$TEMP/iconbuild/run1.ico"`
Run: `dotnet run --project tools/EyeCtrl.IconBuilder -- --out "$TEMP/iconbuild/run2.ico"`
Run: `sha256sum "$TEMP/iconbuild/run1.ico" "$TEMP/iconbuild/run2.ico"`
Expected: **identical hashes**. (SoftwareOnly is set; WPF's PNG encoder writes no timestamps.) If they differ, stop and find the nondeterminism — do not proceed with a varying artifact.

- [ ] **Step 2: Write the real asset**

Run: `dotnet run --project tools/EyeCtrl.IconBuilder` (no `--out` → defaults to `src/EyeCtrl.App/Assets/eyectrl.ico`)
Expected: `wrote ...\src\EyeCtrl.App\Assets\eyectrl.ico (N bytes)`, N roughly 20–60 KB.

- [ ] **Step 3: Commit only the asset**

```bash
git add src/EyeCtrl.App/Assets/eyectrl.ico
git commit -m "feat(app): regenerate eyectrl.ico — six-frame dark tile + amber pie iris (spec @ 9af69129)"
```

---

### Task 5: Artifact harness — A2, A3, A4a (headless tests)

**Files:**
- Create: `tests/EyeCtrl.App.Tests/Assets/AppIconTests.cs`
- Modify: `tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj` (add `UseWindowsForms`)

**Interfaces:**
- Consumes: the committed artifact (Task 4); `WpfApplicationGate.EnsureApplicationWithTokens()` (`EyeCtrl.App.Tests.Fixtures` — required for pack URIs in tests); repo conventions: global `Xunit` + `NFluent` usings from `tests/Directory.Build.props`, `[StaFact]` from `Xunit.StaFact 3.0.13`.
- Produces: the A2/A3/A4a acceptance tests. Task 6 adds A4b to this file.

- [ ] **Step 1: Add `UseWindowsForms` to the test csproj** — inside the first `<PropertyGroup>` of `tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj`, add:

```xml
    <UseWindowsForms>true</UseWindowsForms>
```

(needed for `System.Drawing.Icon`/`Bitmap.GetPixel` in the probes; the App project itself already carries it.)

- [ ] **Step 2: Write the harness tests** — these are acceptance tests of the artifact Task 4 just swapped in, so they are expected to pass on first run (the same assertions fail against the old all-PNG icon, which is exactly what they exist to catch). Create `tests/EyeCtrl.App.Tests/Assets/AppIconTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Assets;

using System.IO;
using System.Windows.Media;
using EyeCtrl.App.Tests.Fixtures;

/// <summary>
/// Acceptance harness for the regenerated eyectrl.ico — spec A2 (container layout),
/// A3 (splash frame pin), A4a (explicit-size frame resolution + pixel probes).
/// Headless: no window, no app launch; the pack URI needs the WpfApplicationGate.
/// </summary>
public class AppIconTests
{
    private const string PackUri = "pack://application:,,,/Assets/eyectrl.ico";

    private static byte[] ReadIconResource()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var resource = System.Windows.Application.GetResourceStream(new Uri(PackUri));
        Check.That(resource).IsNotNull();
        using var ms = new MemoryStream();
        resource!.Stream.CopyTo(ms);
        return ms.ToArray();
    }

    // A2 — 6 entries ascending 16/32/48/64/128/256; BMP 16-128 (biHeight = 2h, 32bpp, BI_RGB),
    // PNG 256; all entries planes 1 / bitcount 32 / colour count 0; payloads contiguous.
    [StaFact]
    public void Ico_HasSixAscendingFrames_WithSpecifiedEncoding()
    {
        var data = ReadIconResource();
        Check.That(data.Length).IsStrictlyGreaterThan(102);
        Check.That((int)BitConverter.ToUInt16(data, 0)).IsEqualTo(0);   // reserved
        Check.That((int)BitConverter.ToUInt16(data, 2)).IsEqualTo(1);   // type icon
        var count = BitConverter.ToUInt16(data, 4);
        Check.That((int)count).IsEqualTo(6);

        int[] expected = [16, 32, 48, 64, 128, 256];
        int previousEnd = 6 + 16 * count;
        for (int i = 0; i < count; i++)
        {
            int entry = 6 + 16 * i;
            int w = data[entry];
            int h = data[entry + 1];
            int colorCount = data[entry + 2];
            int reserved = data[entry + 3];
            int planes = BitConverter.ToUInt16(data, entry + 4);
            int bitCount = BitConverter.ToUInt16(data, entry + 6);
            int bytesInRes = (int)BitConverter.ToUInt32(data, entry + 8);
            int imageOffset = (int)BitConverter.ToUInt32(data, entry + 12);
            int size = w == 0 ? 256 : w;

            Check.That(size).IsEqualTo(expected[i]);
            Check.That(h).IsEqualTo(w);                       // 256 encodes as 0 on both axes
            Check.That(colorCount).IsEqualTo(0);
            Check.That(reserved).IsEqualTo(0);
            Check.That(planes).IsEqualTo(1);
            Check.That(bitCount).IsEqualTo(32);
            Check.That(imageOffset).IsEqualTo(previousEnd);

            var payload = data[imageOffset..(imageOffset + bytesInRes)];
            if (size == 256)
            {
                Check.That(payload[..4]).IsEqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47 }); // PNG sig
            }
            else
            {
                Check.That(BitConverter.ToInt32(payload, 0)).IsEqualTo(40);                // biSize
                Check.That(BitConverter.ToInt32(payload, 8)).IsEqualTo(size * 2);          // biHeight
                Check.That((int)BitConverter.ToUInt16(payload, 14)).IsEqualTo(32);         // biBitCount
                Check.That(BitConverter.ToUInt32(payload, 16)).IsEqualTo(0u);              // BI_RGB
            }
            previousEnd = imageOffset + bytesInRes;
        }
        Check.That(previousEnd).IsEqualTo(data.Length);       // no trailing bytes
    }

    // A3 — WPF shows WIC's first-enumerated frame; with the BMP-16..128 + PNG-256 layout that
    // is the 128 px frame (r2-measured). SplashWindow's XAML string goes through the same
    // ImageSourceConverter path, so converting the full pack URI string mirrors production.
    [StaFact]
    public void Splash_Decode_Yields128PxFrame()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var source = (ImageSource)new ImageSourceConverter().ConvertFrom(PackUri)!;
        Check.That(((BitmapSource)source).PixelWidth).IsEqualTo(128);
    }

    // A4a — explicit sizes only: SmallIconSize keys would break on CI (16 at 96 DPI).
    // Probes are colour sanity at structural points (tile/body/pie/dot); A2 is the
    // structural guarantee for "which frame is in the file".
    [StaFact]
    public void Ico_ExplicitSizeRequest_ReturnsTunedSmallFrames()
    {
        var data = ReadIconResource();

        using var stream32 = new MemoryStream(data);
        using var icon32 = new System.Drawing.Icon(stream32, new System.Drawing.Size(32, 32));
        Check.That(icon32.Width).IsEqualTo(32);
        Check.That(icon32.Height).IsEqualTo(32);
        using var bmp32 = icon32.ToBitmap();
        ProbeDarkTile(bmp32, 4, 4);       // (13.5,13.5)@96 — inside the tile, clear of the corner cut
        ProbeBody(bmp32, 10, 15);         // (30,44)@96 almond fill, clear of iris + edges
        ProbeAmberPie(bmp32, 16, 16);     // (48,48)@96 pie apex
        ProbeBrightDot(bmp32, 20, 16);    // (61,48)@96 gaze dot on the pie head

        using var stream16 = new MemoryStream(data);
        using var icon16 = new System.Drawing.Icon(stream16, new System.Drawing.Size(16, 16));
        Check.That(icon16.Width).IsEqualTo(16);
        Check.That(icon16.Height).IsEqualTo(16);
        using var bmp16 = icon16.ToBitmap();
        ProbeDarkTile(bmp16, 3, 3);       // (21,21)@96 — inside the tile, clear of the corner cut
        ProbeAmberPie(bmp16, 8, 8);       // centre = solid #E8A33C pie
        ProbeBrightDot(bmp16, 10, 8);     // (62.5,48)@96 dot
    }

    private static void ProbeDarkTile(System.Drawing.Bitmap bmp, int x, int y)
    {
        var c = bmp.GetPixel(x, y);
        Check.That((int)c.R).IsStrictlyLessThan(64);
        Check.That((int)c.G).IsStrictlyLessThan(64);
        Check.That((int)c.B).IsStrictlyLessThan(96);
    }

    private static void ProbeBody(System.Drawing.Bitmap bmp, int x, int y)
    {
        var c = bmp.GetPixel(x, y);
        Check.That((int)c.B).IsStrictlyGreaterThan(205);  // body is the only blue-light region
        Check.That((int)c.G).IsStrictlyGreaterThan(165);
        Check.That((int)c.R).IsStrictlyLessThan(170);
    }

    private static void ProbeAmberPie(System.Drawing.Bitmap bmp, int x, int y)
    {
        var c = bmp.GetPixel(x, y);
        Check.That((int)c.R).IsStrictlyGreaterThan(200);
        Check.That((int)c.G).IsStrictlyGreaterThan(140);
        Check.That((int)c.B).IsStrictlyLessThan(160);
    }

    private static void ProbeBrightDot(System.Drawing.Bitmap bmp, int x, int y)
    {
        var c = bmp.GetPixel(x, y);
        Check.That((int)c.R).IsStrictlyGreaterThan(215);
        Check.That((int)c.G).IsStrictlyGreaterThan(225);
        Check.That((int)c.B).IsStrictlyGreaterThan(230);
    }
}
```

- [ ] **Step 3: Run the harness**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~AppIconTests"`
Expected: 3 tests PASS. (Never set `EYECTRL_UI_TESTS`.)

If `Splash_Decode_Yields128PxFrame` fails on `ConvertFrom`, check the actual `PixelWidth` in the failure message before touching anything: 128 means an assertion-plumbing bug; anything else means the WIC enumeration changed and the SPEC (A3/producer layout) must be revisited with the owner — do not silently change the test's expectation.

- [ ] **Step 4: Run the whole App.Tests project** (the icon swap could affect `OverlayResourceResolutionTests`, which constructs `SplashWindow` and parses the icon)

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`
Expected: 0 failed.

- [ ] **Step 5: Commit**

```bash
git add tests/EyeCtrl.App.Tests
git commit -m "test(app): icon acceptance harness — A2 container layout, A3 splash 128px pin, A4a explicit-size probes"
```

---

### Task 6: L10 — `TrayIconManager` loader refactor + A4b pin

**Files:**
- Modify: `src/EyeCtrl.App/Services/TrayIconManager.cs` (ctor body around line 75-92, plus one new method)
- Modify: `tests/EyeCtrl.App.Tests/Assets/AppIconTests.cs` (append A4b test)

**Interfaces:**
- Consumes: nothing new (uses the pack resource already loaded in the ctor).
- Produces: `internal static System.Drawing.Icon TrayIconManager.LoadTrayIcon(Stream stream, System.Drawing.Size size)` — the A4b pin target; spec L10's code shape.

- [ ] **Step 1: Write the failing A4b test** — append to `AppIconTests`:

```csharp
    // A4b — pin the production call shape: the loader must request an explicit size
    // (the ctor passes SystemInformation.SmallIconSize), never the bare Icon(Stream)
    // that picks the large frame (64 px at 200 % DPI). TrayIconManager cannot be
    // constructed in a test (its ctor makes a visible NotifyIcon), so the extracted
    // static is the pin target.
    [StaFact]
    public void LoadTrayIcon_ExplicitSize_ResolvesRequestedFrame()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var resource = System.Windows.Application.GetResourceStream(new Uri(PackUri));
        Check.That(resource).IsNotNull();
        using var icon = EyeCtrl.App.Services.TrayIconManager.LoadTrayIcon(
            resource!.Stream, new System.Drawing.Size(32, 32));
        Check.That(icon.Width).IsEqualTo(32);
        Check.That(icon.Height).IsEqualTo(32);
    }
```

- [ ] **Step 2: Run it to see it fail**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~LoadTrayIcon"`
Expected: **compile error** — `LoadTrayIcon` does not exist yet.

- [ ] **Step 3: Refactor `TrayIconManager`** — in the ctor, replace:

```csharp
                using (resourceInfo.Stream)
                using (var memoryStream = new MemoryStream())
                {
                    resourceInfo.Stream.CopyTo(memoryStream);
                    memoryStream.Position = 0;
                    // Icon constructor copies the data, so we can dispose the stream after
                    _customIcon = new Icon(memoryStream);
                    trayIcon = _customIcon;
                }
```

with:

```csharp
                using (resourceInfo.Stream)
                {
                    // L10/A4b: request the small-icon size so the tray gets the tuned small
                    // frame (32 px at 200 % DPI) instead of the large frame that
                    // Icon(Stream) picks and the shell then shrinks.
                    _customIcon = LoadTrayIcon(resourceInfo.Stream, SystemInformation.SmallIconSize);
                    trayIcon = _customIcon;
                }
```

and add the method directly after the constructor (before the next member):

```csharp
    /// <summary>
    /// Loads the tray icon at an explicit size. Internal static so App.Tests can pin the
    /// size-requesting call (spec A4b): the ctor passes <see cref="System.Windows.Forms.SystemInformation.SmallIconSize"/>.
    /// </summary>
    internal static Icon LoadTrayIcon(Stream stream, System.Drawing.Size size)
    {
        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);
        memoryStream.Position = 0;
        // Icon constructor copies the data, so the caller's stream can be disposed after
        return new Icon(memoryStream, size);
    }
```

(The file already has `using System.Windows.Forms;` — `SystemInformation` resolves; `Icon`/`Stream`/`MemoryStream` are already in use in this file.)

- [ ] **Step 4: Run the pin test, then the whole project**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~LoadTrayIcon"` → PASS.
Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"` → 0 failed.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Services/TrayIconManager.cs tests/EyeCtrl.App.Tests/Assets/AppIconTests.cs
git commit -m "feat(app): tray requests SmallIconSize (L10) — extract testable LoadTrayIcon + A4b pin"
```

---

### Task 7: Final gate + owner handoff (A6)

**Files:** none created. Verification only.

- [ ] **Step 1: Full build**

Run: `dotnet build EyeCtrl.slnx`
Expected: Build succeeded, 0 errors (the builder is outside the solution and must not appear in the output).

- [ ] **Step 2: Full non-UI suite**

Run: `dotnet test --filter "Category!=UI"`
Expected: 0 failed across all projects. (`EYECTRL_UI_TESTS` must not be set in the environment — check `echo $EYECTRL_UI_TESTS` first; it must print nothing.)

- [ ] **Step 3: Refresh the icon cache**

Run: `ie4uinit.exe -show` (no Explorer restart needed).

- [ ] **Step 4: Owner A6 handoff** — report to the owner, in these words' spirit:

> The new icon is committed. When you choose to launch EyeCtrl, check the tray and splash; check the EyeCtrl.exe file icon in Explorer (after the cache refresh in step 3). Do not let me launch the app for you.

The executor must **not** launch the app, not even with `--test-mode`. A6 completes when the owner confirms what they see.

- [ ] **Step 5: Commit any straggler** (e.g. spec addendum recording A5/A6 completion); if nothing changed, skip.

```bash
git status --porcelain   # confirm clean
```
