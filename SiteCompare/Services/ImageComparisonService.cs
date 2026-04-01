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

    public ComparisonResult Compare(byte[] imageA, byte[] imageB)
    {
        _logger.LogDebug("Starting image comparison: imageA={SizeA} bytes, imageB={SizeB} bytes", imageA.Length, imageB.Length);

        using var imgA = Image.Load<Rgba32>(imageA);
        using var imgB = Image.Load<Rgba32>(imageB);

        // Normalize sizes — pad to the larger dimensions so we compare identical canvas sizes
        int targetWidth = Math.Max(imgA.Width, imgB.Width);
        int targetHeight = Math.Max(imgA.Height, imgB.Height);

        if (imgA.Width != imgB.Width || imgA.Height != imgB.Height)
        {
            _logger.LogDebug("Image dimensions differ — normalizing canvas to {Width}x{Height} (imageA: {WA}x{HA}, imageB: {WB}x{HB})",
                targetWidth, targetHeight, imgA.Width, imgA.Height, imgB.Width, imgB.Height);
        }

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

                    if (PixelsDiffer(pixA, pixB))
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

        _logger.LogDebug(
            "Image comparison: {Different}/{Total} different pixels ({Percentage:F2}%)",
            differentPixels, totalPixels, differencePercentage);

        if (differencePercentage > 50.0)
        {
            _logger.LogWarning("High pixel difference detected: {Percentage:F2}% — pages may be substantially different", differencePercentage);
        }

        using var diffImage = Image.LoadPixelData<Rgba32>(diffPixels, targetWidth, targetHeight);
        byte[] diffBytes;
        using (var ms = new MemoryStream())
        {
            diffImage.SaveAsPng(ms);
            diffBytes = ms.ToArray();
        }

        _logger.LogDebug("Diff image generated: {DiffBytes} bytes", diffBytes.Length);

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
}
