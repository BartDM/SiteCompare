namespace SiteCompare.Services;

public interface IImageComparisonService
{
    ComparisonResult Compare(byte[] imageA, byte[] imageB, bool ignoreWhitespaceDifferences = false);
}

public class ComparisonResult
{
    public double DifferencePercentage { get; set; }
    public bool HasDifferences { get; set; }
    public byte[]? DiffImage { get; set; }
}
