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

using CommunityToolkit.Mvvm.ComponentModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;

namespace NINA.Core.Utility;

public abstract class BaseINPC : ObservableObject {

    protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null) {
        OnPropertyChanged(new PropertyChangedEventArgs(propertyName));
    }

    protected void ChildChanged(object? sender, PropertyChangedEventArgs e) {
        RaisePropertyChanged("IsChanged");
    }

    protected void Items_CollectionChanged(object? sender,
           System.Collections.Specialized.NotifyCollectionChangedEventArgs e) {
        if (e.OldItems != null) {
            foreach (INotifyPropertyChanged item in e.OldItems) {
                item.PropertyChanged -= Item_PropertyChanged;
            }
        }
        if (e.NewItems != null) {
            foreach (INotifyPropertyChanged item in e.NewItems) {
                item.PropertyChanged += Item_PropertyChanged;
            }
        }
    }

    protected void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e) {
        RaisePropertyChanged("IsChanged");
    }

    protected void RaiseAllPropertiesChanged() {
        OnPropertyChanged(new PropertyChangedEventArgs(null));
    }
}

[Serializable]
[DataContract]
[Obsolete("This class is used for migration purposes when serialization attribute is required")]
public abstract class SerializableINPC : INotifyPropertyChanged {

    protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null) {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    [field: NonSerialized]
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void RaiseAllPropertiesChanged() {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}