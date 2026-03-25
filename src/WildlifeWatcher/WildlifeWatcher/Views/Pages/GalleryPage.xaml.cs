using System.Windows.Controls;
using WildlifeWatcher.ViewModels;

namespace WildlifeWatcher.Views.Pages;

public partial class GalleryPage : UserControl
{
    private readonly GalleryViewModel _vm;

    public GalleryPage(GalleryViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.LoadAsync();
    }

    // Fires whenever the captures ListBox is scrolled. Triggers the next page load
    // automatically when the user scrolls within 300px of the bottom, replacing the
    // old manual "Load More" button.
    private void OnCapturesScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_vm.HasMoreCaptures || _vm.IsLoadingMoreCaptures) return;

        // Load more when near the bottom OR when all current items fit in the viewport
        // (so a short list auto-fills without requiring the user to scroll at all).
        bool nearBottom   = e.ExtentHeight > 0 &&
                            e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 300;
        bool viewportFull = e.ExtentHeight <= e.ViewportHeight;

        if (nearBottom || viewportFull)
            _ = _vm.LoadMoreCapturesCommand.ExecuteAsync(null);
    }
}
