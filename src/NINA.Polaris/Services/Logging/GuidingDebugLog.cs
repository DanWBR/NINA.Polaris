// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
// FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License
// for more details. You should have received a copy of the license along with
// this program. If not, see <https://www.gnu.org/licenses/>.

namespace NINA.Polaris.Services.Logging;

/// <summary>
/// Dedicated disk sink for the guiding system's log traffic. The native
/// guider emits messages at frame cadence (star selection, corrections,
/// settling, dark-library activity) which drowned the main LOG panel /
/// app log (field report). <see cref="LogBufferLogger"/> diverts those
/// categories here instead: one <c>guiding_debug_YYYYMMDD.log</c> per
/// local day, written next to the per-session PHD2-format guide logs
/// (<see cref="GuideSessionLogService.GuideLogDir"/>) so everything
/// guiding-related lives in one folder.
///
/// Static + lock-guarded: loggers are created per category by the
/// logging infrastructure and must never take DI dependencies.
/// All failures are swallowed — a full disk can't break guiding.
/// </summary>
public static class GuidingDebugLog {
    private static readonly object _lock = new();
    private static StreamWriter? _writer;
    private static string _openDate = "";

    public static void Write(string level, string category, string message, Exception? exception) {
        try {
            lock (_lock) {
                var today = DateTime.Now.ToString("yyyyMMdd");
                if (_writer == null || _openDate != today) {
                    _writer?.Dispose();
                    Directory.CreateDirectory(GuideSessionLogService.GuideLogDir);
                    var path = Path.Combine(GuideSessionLogService.GuideLogDir,
                        $"guiding_debug_{today}.log");
                    _writer = new StreamWriter(new FileStream(path, FileMode.Append,
                        FileAccess.Write, FileShare.Read)) { AutoFlush = true };
                    _openDate = today;
                }
                var shortCat = category.Contains('.')
                    ? category[(category.LastIndexOf('.') + 1)..] : category;
                _writer.WriteLine(exception == null
                    ? $"{DateTime.Now:HH:mm:ss.fff} [{level}] {shortCat}: {message}"
                    : $"{DateTime.Now:HH:mm:ss.fff} [{level}] {shortCat}: {message} :: {exception.GetType().Name}: {exception.Message}");
            }
        } catch { /* never let logging break the guider */ }
    }
}
