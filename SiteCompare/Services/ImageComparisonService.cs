using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace SiteCompare.Services;

public class ImageComparisonService : IImageComparisonService
{
    private readonly ILogger<ImageComparisonService> _logger;

    public ImageComparisonService(ILogger<ImageComparisonService> logger)
    {
        _logger = logger;
    }

    public ComparisonResult Compare(byte[] imageA, byte[] imageB, bool ignoreWhitespaceDifferences = false)
    {
        using var imgA = Image.Load<Rgba32>(imageA);
        using var imgB = Image.Load<Rgba32>(imageB);

        // Normalize sizes — pad to the larger dimensions so we compare identical canvas sizes
        int targetWidth = Math.Max(imgA.Width, imgB.Width);
        int targetHeight = Math.Max(imgA.Height, imgB.Height);

        using var normalA = NormalizeTo(imgA, targetWidth, targetHeight);
        using var normalB = NormalizeTo(imgB, targetWidth, targetHeight);

        long differentPixels = 0;
        long totalPixels = (long)targetWidth * targetHeight;

        // Build diff image pixel data
        var diffPixels = new Rgba32[targetWidth * targetHeight];

        normalA.ProcessPixelRows(normalB, (accessorA, accessorB) =>
        {
            for (int y = 0; y < accessorA.Height; y++)
            {
                var rowA = accessorA.GetRowSpan(y);
                var rowB = accessorB.GetRowSpan(y);

                for (int x = 0; x < rowA.Length; x++)
                {
                    var pixA = rowA[x];
                    var pixB = rowB[x];

                    if (PixelsDiffer(pixA, pixB) && !(ignoreWhitespaceDifferences && BothWhitespace(pixA, pixB)))
                    {
                        System.Threading.Interlocked.Increment(ref differentPixels);
                        diffPixels[y * targetWidth + x] = new Rgba32(255, 0, 0, 200);
                    }
                    else
                    {
                        // Darken unchanged pixels for context
                        diffPixels[y * targetWidth + x] = new Rgba32(
                            (byte)(pixA.R * 0.3),
                            (byte)(pixA.G * 0.3),
                            (byte)(pixA.B * 0.3),
                            255);
                    }
                }
            }
        });

        var differencePercentage = totalPixels > 0
            ? (double)differentPixels / totalPixels * 100.0
            : 0.0;

        using var diffImage = Image.LoadPixelData<Rgba32>(diffPixels, targetWidth, targetHeight);
        byte[] diffBytes;
        using (var ms = new MemoryStream())
        {
            diffImage.SaveAsPng(ms);
            diffBytes = ms.ToArray();
        }

        _logger.LogDebug(
            "Image comparison: {Different}/{Total} different pixels ({Percentage:F2}%)",
            differentPixels, totalPixels, differencePercentage);

        return new ComparisonResult
        {
            DifferencePercentage = Math.Round(differencePercentage, 4),
            HasDifferences = differencePercentage > 0,
            DiffImage = diffBytes
        };
    }

    private static Image<Rgba32> NormalizeTo(Image<Rgba32> source, int width, int height)
    {
        var clone = source.Clone(ctx =>
        {
            if (source.Width != width || source.Height != height)
            {
                ctx.Pad(width, height, Color.White);
            }
        });

        return clone;
    }

    private static bool PixelsDiffer(Rgba32 a, Rgba32 b)
    {
        // Allow a small tolerance to avoid noise from anti-aliasing and sub-pixel rendering differences
        const int tolerance = 8;
        return Math.Abs(a.R - b.R) > tolerance
            || Math.Abs(a.G - b.G) > tolerance
            || Math.Abs(a.B - b.B) > tolerance;
    }

    /// <summary>
    /// Returns true when both pixels are white or near-white (i.e. blank whitespace regions).
    /// Differences between such pixels are ignored when <c>IgnoreWhitespaceDifferences</c> is enabled,
    /// because they represent empty space that has not caused any visible content to shift.
    /// If whitespace addition shifts content elements, those elements will appear at different positions
    /// in the two images as non-white pixels, so they will still be detected as differences.
    /// </summary>
    private static bool BothWhitespace(Rgba32 a, Rgba32 b)
    {
        const int whiteThreshold = 240;
        return a.R >= whiteThreshold && a.G >= whiteThreshold && a.B >= whiteThreshold
            && b.R >= whiteThreshold && b.G >= whiteThreshold && b.B >= whiteThreshold;
    }
}
