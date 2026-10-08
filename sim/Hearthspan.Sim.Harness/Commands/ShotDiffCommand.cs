using System.Drawing;
using System.Drawing.Imaging;

namespace Hearthspan.Sim.Harness.Commands;

/// <summary>hearthspan shot-diff --a a.png --b b.png [--out diff.png] [--threshold 0.02] — per-pixel
/// PNG comparison for presentation-change verification (docs/development-plan.md Phase 3 item 5).
/// Exit 0 when the changed-pixel ratio is within threshold, 1 otherwise — a meaningful exit code, not
/// just a log line to scrape.</summary>
internal static class ShotDiffCommand
{
    public static int Run(string[] args)
    {
        var options = ArgParser.Parse(args);
        string? aPath = options.GetOrDefault("a");
        string? bPath = options.GetOrDefault("b");
        if (aPath == null || bPath == null)
        {
            return HarnessOutput.Fail("shot-diff requires --a a.png and --b b.png");
        }
        if (!File.Exists(aPath) || !File.Exists(bPath))
        {
            return HarnessOutput.Fail($"shot-diff: file not found (a exists={File.Exists(aPath)}, b exists={File.Exists(bPath)})");
        }

        double threshold = ParseDouble(options.GetOrDefault("threshold"), 0.02);
        string? outPath = options.GetOrDefault("out");

        using var a = new Bitmap(aPath);
        using var b = new Bitmap(bPath);

        if (a.Width != b.Width || a.Height != b.Height)
        {
            return HarnessOutput.Result(new
            {
                status = "error",
                message = "dimension mismatch",
                aSize = new { a.Width, a.Height },
                bSize = new { b.Width, b.Height },
            }, false);
        }

        Bitmap? diff = outPath != null ? new Bitmap(a.Width, a.Height, PixelFormat.Format32bppArgb) : null;
        long changed = 0;
        long total = (long)a.Width * a.Height;

        for (int y = 0; y < a.Height; y++)
        {
            for (int x = 0; x < a.Width; x++)
            {
                var pa = a.GetPixel(x, y);
                var pb = b.GetPixel(x, y);
                bool different = Math.Abs(pa.R - pb.R) + Math.Abs(pa.G - pb.G) + Math.Abs(pa.B - pb.B) > 15;
                if (different)
                {
                    changed++;
                    diff?.SetPixel(x, y, Color.Magenta);
                }
                else
                {
                    diff?.SetPixel(x, y, Color.FromArgb(40, pa.R, pa.G, pa.B));
                }
            }
        }

        double ratio = total == 0 ? 0 : (double)changed / total;
        if (outPath != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            diff!.Save(outPath, ImageFormat.Png);
            diff.Dispose();
        }

        bool withinThreshold = ratio <= threshold;
        return HarnessOutput.Result(new
        {
            status = withinThreshold ? "ok" : "diff-exceeds-threshold",
            aPath,
            bPath,
            outPath,
            width = a.Width,
            height = a.Height,
            changedPixels = changed,
            totalPixels = total,
            changedRatio = ratio,
            threshold,
        }, withinThreshold);
    }

    private static double ParseDouble(string? s, double fallback) =>
        s != null && double.TryParse(s, out var v) ? v : fallback;
}
