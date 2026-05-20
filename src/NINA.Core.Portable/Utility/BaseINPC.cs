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
