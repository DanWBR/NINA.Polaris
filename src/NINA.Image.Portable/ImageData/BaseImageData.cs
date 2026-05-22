using NINA.Image.Interfaces;

namespace NINA.Image.ImageData;

public class BaseImageData : IImageData, IHasRawFile {
    public ImageProperties Properties { get; }
    public ImageMetaData MetaData { get; }
    public ushort[] Data { get; }

    private IImageStatistics? _statistics;
    public IImageStatistics Statistics => _statistics ??= ImageStatistics.Create(this);

    /// <summary>Optional vendor-native RAW bytes attached by DSLR /
    /// mirrorless drivers. Null for everything else. See
    /// <see cref="IHasRawFile"/> for the persistence contract.</summary>
    public byte[]? RawFileBytes { get; set; }
    public string? RawFileExtension { get; set; }

    public BaseImageData(ushort[] data, ImageProperties properties, ImageMetaData? metaData = null) {
        Data = data ?? throw new ArgumentNullException(nameof(data));
        Properties = properties;
        MetaData = metaData ?? new ImageMetaData();
    }
}
