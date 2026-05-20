using System.Diagnostics;
using System.Reflection;

namespace NINA.Core.Utility;

public static class CoreUtil {
    public static char[] PATHSEPARATORS = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];
    public static string APPLICATIONDIRECTORY = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
    public static string APPLICATIONTEMPPATH = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA");
    public static DateTime ApplicationStartDate = DateTime.Now;
    public static DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static string Version {
        get {
            var assembly = Assembly.GetExecutingAssembly();
            var fvi = FileVersionInfo.GetVersionInfo(assembly.Location);
            return fvi.FileVersion ?? "0.0.0.0";
        }
    }

    public static string Title => "N.I.N.A. Headless";

    public static string GetUniqueFilePath(string fullPath) {
        int count = 1;
        string fileNameOnly = Path.GetFileNameWithoutExtension(fullPath);
        string extension = Path.GetExtension(fullPath);
        string path = Path.GetDirectoryName(fullPath)!;
        string newFullPath = fullPath;

        while (File.Exists(newFullPath)) {
            string tempFileName = $"{fileNameOnly}({count++})";
            newFullPath = Path.Combine(path, tempFileName + extension);
        }
        return newFullPath;
    }
}
