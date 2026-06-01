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
    /// <summary>Optional per-update explanation from the driver — INDI
    /// servers can attach <c>message="..."</c> on any set*Vector, and
    /// typically do so when reporting state=Alert (e.g. "Mount is parked",
    /// "Below horizon", "Slew limit exceeded"). Captured here so the
    /// ack-based write API (<c>SetNumberAsyncAck</c> /
    /// <c>SetSwitchAsyncAck</c>) can bubble it up to the operator as a
    /// real error message instead of a generic "slew failed".</summary>
    public string? Message { get; set; }
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
