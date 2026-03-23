using System.Windows;
using System.Windows.Controls;
using WildlifeWatcher.Services.Interfaces;

namespace WildlifeWatcher.Views.Dialogs;

public partial class ReassignSpeciesDialog : Window
{
    private record SpeciesItem(int Id, string CommonName, string ScientificName);

    private List<SpeciesItem> _allSpecies = new();

    public int? SelectedSpeciesId { get; private set; }

    public ReassignSpeciesDialog(IReadOnlyList<SpeciesSummary> species)
    {
        InitializeComponent();
        _allSpecies = species
            .Select(s => new SpeciesItem(s.SpeciesId, s.CommonName, s.ScientificName))
            .ToList();
        ApplyFilter(string.Empty);
        SearchBox.Focus();
    }

    private void ApplyFilter(string query)
    {
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _allSpecies
            : _allSpecies.Where(s =>
                s.CommonName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.ScientificName.Contains(query, StringComparison.OrdinalIgnoreCase));

        SpeciesList.ItemsSource = filtered.ToList();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        => ApplyFilter(SearchBox.Text);

    private void SpeciesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => AssignButton.IsEnabled = SpeciesList.SelectedItem != null;

    private void Assign_Click(object sender, RoutedEventArgs e)
    {
        if (SpeciesList.SelectedItem is SpeciesItem item)
        {
            SelectedSpeciesId = item.Id;
            DialogResult = true;
            Close();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
