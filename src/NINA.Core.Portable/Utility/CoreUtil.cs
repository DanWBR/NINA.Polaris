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

    public static string Title => "N.I.N.A. Polaris";

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