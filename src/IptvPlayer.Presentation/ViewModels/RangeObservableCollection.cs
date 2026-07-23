using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace IptvPlayer.Presentation.ViewModels;

internal sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceRange(IEnumerable<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        Items.Clear();
        foreach (var value in values)
        {
            Items.Add(value);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
