using Newtonsoft.Json;
using System.Xml.Serialization;

namespace NINA.Core.Model.Equipment;

[JsonObject(MemberSerialization.OptIn)]
[Serializable]
[XmlRoot(ElementName = nameof(BinningMode))]
public class BinningMode {
    private const char SEPARATOR = 'x';

    private BinningMode() { }

    public BinningMode(short x, short y) {
        X = x;
        Y = y;
    }

    public string Name => string.Join(SEPARATOR.ToString(), X, Y);

    [XmlElement(nameof(X))]
    [JsonProperty(PropertyName = nameof(X))]
    public short X { get; set; }

    [XmlElement(nameof(Y))]
    [JsonProperty(PropertyName = nameof(Y))]
    public short Y { get; set; }

    public override string ToString() => Name;

    public override bool Equals(object? obj) {
        if (obj is not BinningMode other) return false;
        return X == other.X && Y == other.Y;
    }

    public override int GetHashCode() {
        const int primeNumber = 397;
        unchecked {
            return (X.GetHashCode() * primeNumber) ^ Y.GetHashCode();
        }
    }

    public static bool TryParse(string s, out BinningMode? mode) {
        mode = null;
        if (string.IsNullOrEmpty(s)) return false;
        var parts = s.Split(SEPARATOR);
        if (parts.Length != 2) return false;
        if (!short.TryParse(parts[0], out short x)) return false;
        if (!short.TryParse(parts[1], out short y)) return false;
        mode = new BinningMode(x, y);
        return true;
    }
}
