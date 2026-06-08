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

using NINA.Core.Enum;

namespace NINA.Image.ImageData;

public class ImageMetaData {
    public CameraInfo Camera { get; set; } = new();
    public TelescopeInfo Telescope { get; set; } = new();
    public ObserverInfo Observer { get; set; } = new();
    public TargetInfo Target { get; set; } = new();
    public ExposureInfo Exposure { get; set; } = new();
    public FilterWheelInfo FilterWheel { get; set; } = new();
    public FocuserInfo Focuser { get; set; } = new();
    public WeatherInfo Weather { get; set; } = new();
    public DateTime CreationTime { get; set; } = DateTime.UtcNow;

    public class CameraInfo {
        public string Name { get; set; } = string.Empty;
        public double Temperature { get; set; }
        public int Gain { get; set; }
        public int Offset { get; set; }
        public short BinX { get; set; } = 1;
        public short BinY { get; set; } = 1;
        public double PixelSizeX { get; set; }
        public double PixelSizeY { get; set; }
        public SensorType SensorType { get; set; }
        public BayerPatternEnum BayerPattern { get; set; } = BayerPatternEnum.None;
        public int ReadoutMode { get; set; }
    }

    public class TelescopeInfo {
        public string Name { get; set; } = string.Empty;
        public double FocalLength { get; set; }
        public double FocalRatio { get; set; }
        public double RightAscension { get; set; }
        public double Declination { get; set; }
        public double Altitude { get; set; }
        public double Azimuth { get; set; }
        public PierSide SideOfPier { get; set; } = PierSide.pierUnknown;
    }

    public class ObserverInfo {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double Elevation { get; set; }
    }

    public class TargetInfo {
        public string Name { get; set; } = string.Empty;
        public double RightAscension { get; set; }
        public double Declination { get; set; }
        public double Rotation { get; set; }
    }

    public class ExposureInfo {
        public double ExposureTime { get; set; }
        public string Filter { get; set; } = string.Empty;
        public int ExposureNumber { get; set; }
        public string ImageType { get; set; } = "LIGHT";
    }

    public class FilterWheelInfo {
        public string Name { get; set; } = string.Empty;
        public string Filter { get; set; } = string.Empty;
        public int Position { get; set; }
    }

    public class FocuserInfo {
        public string Name { get; set; } = string.Empty;
        public int Position { get; set; }
        public double Temperature { get; set; }
        public double StepSize { get; set; }
    }

    public class WeatherInfo {
        public double Temperature { get; set; }
        public double Humidity { get; set; }
        public double DewPoint { get; set; }
        public double Pressure { get; set; }
        public double SkyBrightness { get; set; }
        public double SkyQuality { get; set; }
    }
}