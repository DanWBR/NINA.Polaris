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