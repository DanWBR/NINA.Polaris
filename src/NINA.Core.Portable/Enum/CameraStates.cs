namespace NINA.Core.Enum;

public enum CameraStates {
    NoState = -1,
    Idle = 0,
    Waiting,
    Exposing,
    Reading,
    Download,
    Error,
    LoadingFile = 100,
}
