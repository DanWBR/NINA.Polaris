// Copyright (C) 2016-2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This file is derived from N.I.N.A. - Nighttime Imaging 'N' Astronomy.
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// As part of N.I.N.A. Polaris this file is additionally available under the
// GNU Affero General Public License v3.0 (see LICENSE.txt and NOTICE), at the
// recipient's option, pursuant to MPL-2.0 section 3.3.

// Copyright (C) 2016-2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This file is derived from N.I.N.A. - Nighttime Imaging 'N' Astronomy.
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// As part of N.I.N.A. Polaris this file is additionally available under the
// GNU Affero General Public License v3.0 (see LICENSE.txt and NOTICE), at the
// recipient's option, pursuant to MPL-2.0 section 3.3.

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