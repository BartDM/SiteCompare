namespace SiteCompare.Services;

public interface IImageComparisonService
{
    ComparisonResult Compare(byte[] imageA, byte[] imageB);
}

public class ComparisonResult
{
    public double DifferencePercentage { get; set; }
    public bool HasDifferences { get; set; }
    public byte[]? DiffImage { get; set; }
}
