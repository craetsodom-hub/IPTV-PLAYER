namespace IptvPlayer.Contracts.Import;

public readonly record struct SourceImportProgress(double Percent)
{
    public double Percent { get; } = Math.Clamp(Percent, 0d, 100d);
}
