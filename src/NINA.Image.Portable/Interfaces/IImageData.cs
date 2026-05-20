using NINA.Image.ImageData;

namespace NINA.Image.Interfaces;

public interface IImageData {
    ImageProperties Properties { get; }
    ImageMetaData MetaData { get; }
    ushort[] Data { get; }
    IImageStatistics Statistics { get; }
}
