using NINA.Image.Interfaces;

namespace NINA.Image.ImageData;

public class BaseImageData : IImageData {
    public ImageProperties Properties { get; }
    public ImageMetaData MetaData { get; }
    public ushort[] Data { get; }

    private IImageStatistics? _statistics;
    public IImageStatistics Statistics => _statistics ??= ImageStatistics.Create(this);

    public BaseImageData(ushort[] data, ImageProperties properties, ImageMetaData? metaData = null) {
        Data = data ?? throw new ArgumentNullException(nameof(data));
        Properties = properties;
        MetaData = metaData ?? new ImageMetaData();
    }
}
