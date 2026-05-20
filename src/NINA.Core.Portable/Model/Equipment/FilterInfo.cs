using Newtonsoft.Json;

namespace NINA.Core.Model.Equipment;

[JsonObject(MemberSerialization.OptIn)]
public class FilterInfo {
    [JsonProperty] public string Name { get; set; } = string.Empty;
    [JsonProperty] public int Position { get; set; }
    [JsonProperty] public double FocusOffset { get; set; }
    [JsonProperty] public short AutoFocusExposureTime { get; set; } = 10;
    [JsonProperty] public int? FlatWizardFilterSettingsKey { get; set; }

    public FilterInfo() { }

    public FilterInfo(string name, int position, double focusOffset = 0) {
        Name = name;
        Position = position;
        FocusOffset = focusOffset;
    }

    public override string ToString() => $"{Name} (Pos: {Position})";
}
