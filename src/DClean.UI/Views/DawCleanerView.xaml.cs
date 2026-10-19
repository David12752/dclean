using DClean.UI.ViewModels;
using System.Windows.Controls;
using System.Windows.Input;

namespace DClean.UI.Views;

public partial class DawCleanerView : UserControl
{
    public DawCleanerView() => InitializeComponent();

    private void CacheItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem item &&
            item.DataContext is DClean.Contracts.DawCacheEntry entry &&
            DataContext is DawCleanerViewModel vm)
        {
            if (vm.SelectedCaches.Contains(entry))
                vm.SelectedCaches.Remove(entry);
            else
                vm.SelectedCaches.Add(entry);
        }
    }
}
