namespace NINA.INDI.Protocol;

public enum IndiPropertyState {
    Idle,
    Ok,
    Busy,
    Alert
}

public enum IndiPropertyPermission {
    ReadOnly,
    WriteOnly,
    ReadWrite
}

public enum IndiSwitchRule {
    OneOfMany,
    AtMostOne,
    AnyOfMany
}

public abstract class IndiProperty {
    public string Device { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public IndiPropertyState State { get; set; } = IndiPropertyState.Idle;
    public IndiPropertyPermission Permission { get; set; } = IndiPropertyPermission.ReadOnly;
    public double Timeout { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class IndiTextProperty : IndiProperty {
    public Dictionary<string, string> Values { get; set; } = new();
}

public class IndiNumberProperty : IndiProperty {
    public Dictionary<string, IndiNumberElement> Values { get; set; } = new();
}

public class IndiNumberElement {
    public string Label { get; set; } = string.Empty;
    public double Value { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
    public double Step { get; set; }
    public string Format { get; set; } = "%g";
}

public class IndiSwitchProperty : IndiProperty {
    public IndiSwitchRule Rule { get; set; } = IndiSwitchRule.OneOfMany;
    public Dictionary<string, bool> Values { get; set; } = new();
}

public class IndiLightProperty : IndiProperty {
    public Dictionary<string, IndiPropertyState> Values { get; set; } = new();
}

public class IndiBlobProperty : IndiProperty {
    public Dictionary<string, IndiBlobElement> Values { get; set; } = new();
}

public class IndiBlobElement {
    public string Label { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public int Size { get; set; }
    public byte[]? Data { get; set; }
}
