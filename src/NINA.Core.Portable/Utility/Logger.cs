using Serilog;
using System.Runtime.CompilerServices;

namespace NINA.Core.Utility;

public static class Logger {
    private static ILogger _logger = new LoggerConfiguration()
        .MinimumLevel.Debug()
        .WriteTo.Console()
        .WriteTo.File(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA", "Logs", "nina-polaris-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 10)
        .CreateLogger();

    public static void SetCustomLogger(ILogger logger) {
        _logger = logger;
    }

    public static void Debug(string message, [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFilePath = "", [CallerLineNumber] int sourceLineNumber = 0) {
        _logger.Debug("[{Member}] {Message}", memberName, message);
    }

    public static void Info(string message, [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFilePath = "", [CallerLineNumber] int sourceLineNumber = 0) {
        _logger.Information("[{Member}] {Message}", memberName, message);
    }

    public static void Warning(string message, [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFilePath = "", [CallerLineNumber] int sourceLineNumber = 0) {
        _logger.Warning("[{Member}] {Message}", memberName, message);
    }

    public static void Error(string message, [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFilePath = "", [CallerLineNumber] int sourceLineNumber = 0) {
        _logger.Error("[{Member}] {Message}", memberName, message);
    }

    public static void Error(Exception ex, [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFilePath = "", [CallerLineNumber] int sourceLineNumber = 0) {
        _logger.Error(ex, "[{Member}] Exception", memberName);
    }

    public static void Error(string message, Exception ex, [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFilePath = "", [CallerLineNumber] int sourceLineNumber = 0) {
        _logger.Error(ex, "[{Member}] {Message}", memberName, message);
    }

    public static void Trace(string message, [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFilePath = "", [CallerLineNumber] int sourceLineNumber = 0) {
        _logger.Verbose("[{Member}] {Message}", memberName, message);
    }
}
