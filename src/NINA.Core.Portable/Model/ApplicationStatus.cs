using NINA.Core.Utility;

namespace NINA.Core.Model;

public class ApplicationStatus : BaseINPC {
    private string? _source;
    public string? Source {
        get => _source;
        set { _source = value; RaisePropertyChanged(); }
    }

    private string? _status;
    public string? Status {
        get => _status;
        set { _status = value; RaisePropertyChanged(); }
    }

    private double _progress = -1;
    public double Progress {
        get => _progress;
        set { _progress = value; RaisePropertyChanged(); }
    }

    private int _maxProgress = 1;
    public int MaxProgress {
        get => _maxProgress;
        set { _maxProgress = value; RaisePropertyChanged(); }
    }

    private StatusProgressType _progressType = StatusProgressType.Percent;
    public StatusProgressType ProgressType {
        get => _progressType;
        set { _progressType = value; RaisePropertyChanged(); }
    }

    public enum StatusProgressType {
        Percent,
        ValueOfMaxValue
    }
}
