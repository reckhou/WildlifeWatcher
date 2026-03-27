using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WildlifeWatcher.ViewModels;

namespace WildlifeWatcher.Views.Pages;

public partial class GalleryPage : UserControl
{
    private readonly GalleryViewModel _vm;

    // Per-species scroll memory: species name → vertical offset
    private readonly Dictionary<string, double> _scrollPositions = new();
    // Scroll offsets saved just before a day filter is applied, keyed by species name.
    private readonly Dictionary<string, double> _preFilterScrollPositions = new();
    private ScrollViewer? _capturesScrollViewer;
    // True while a species transition is in progress so spurious ScrollChanged events
    // fired during the reset/layout don't overwrite the new species' saved position.
    private bool _suppressScrollSave;

    public GalleryPage(GalleryViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.LoadAsync();

        // When ResetWith fires (new species opened), suppress saves until after the
        // restore completes so the old scroll position isn't written under the new name.
        viewModel.SelectedCaptures.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
                ScheduleScrollRestore();
        };

        // Also restore when CurrentView changes TO SpeciesDetail (Back → re-open path),
        // because the ListBox is Collapsed during LoadCapturesAsync and ignores scroll calls.
        // Also tracks IsFilteredByDay changes to save/restore pre-filter scroll position.
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GalleryViewModel.IsShowingSpeciesDetail) &&
                viewModel.IsShowingSpeciesDetail)
                ScheduleScrollRestore();

            if (e.PropertyName == nameof(GalleryViewModel.IsFilteredByDay))
            {
                var name = _vm.SelectedSpeciesName;
                if (string.IsNullOrEmpty(name)) return;
                if (_vm.IsFilteredByDay)
                {
                    // Snapshot the current scroll offset before the filtered list loads
                    _preFilterScrollPositions[name] = _scrollPositions.GetValueOrDefault(name, 0.0);
                }
                else
                {
                    // Restore pre-filter offset into _scrollPositions so ScheduleScrollRestore
                    // picks it up naturally without any changes to that path.
                    if (_preFilterScrollPositions.TryGetValue(name, out var offset))
                        _scrollPositions[name] = offset;
                }
            }
        };
    }

    private void ScheduleScrollRestore()
    {
        _suppressScrollSave = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_vm.IsFilteredByDay)
                GetCapturesScrollViewer()?.ScrollToVerticalOffset(0);
            else
                RestoreSpeciesScroll();
            _suppressScrollSave = false;
        });
    }

    private void RestoreSpeciesScroll()
    {
        var sv = GetCapturesScrollViewer();
        if (sv == null) return;
        var offset = _vm.SelectedSpeciesName is { Length: > 0 } name
            ? _scrollPositions.GetValueOrDefault(name, 0.0)
            : 0.0;
        sv.ScrollToVerticalOffset(offset);
    }

    // Fires whenever the captures ListBox is scrolled.
    private void OnCapturesScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Cache the ScrollViewer on first event — OriginalSource is the inner ScrollViewer.
        _capturesScrollViewer ??= e.OriginalSource as ScrollViewer;

        // Persist the scroll position for the current species so it can be restored later.
        // Suppressed during species transitions to prevent the old offset being saved under
        // the new species' name while the layout recalculates after a collection reset.
        if (!_suppressScrollSave && _vm.IsShowingSpeciesDetail && !string.IsNullOrEmpty(_vm.SelectedSpeciesName))
            _scrollPositions[_vm.SelectedSpeciesName] = e.VerticalOffset;

        // Trigger next page load when near the bottom or all items fit in the viewport.
        if (!_vm.HasMoreCaptures || _vm.IsLoadingMoreCaptures) return;

        bool nearBottom   = e.ExtentHeight > 0 &&
                            e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 300;
        bool viewportFull = e.ExtentHeight <= e.ViewportHeight;

        if (nearBottom || viewportFull)
            _ = _vm.LoadMoreCapturesCommand.ExecuteAsync(null);
    }

    private ScrollViewer? GetCapturesScrollViewer()
    {
        if (_capturesScrollViewer != null) return _capturesScrollViewer;
        return _capturesScrollViewer = FindScrollViewer(CapturesListBox);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(element, i);
            if (child is ScrollViewer sv) return sv;
            var result = FindScrollViewer(child);
            if (result != null) return result;
        }
        return null;
    }
}
