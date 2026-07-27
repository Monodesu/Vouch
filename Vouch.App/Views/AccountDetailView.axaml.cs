using Avalonia.Controls;
using Avalonia.Interactivity;
using Vouch.App.ViewModels;

namespace Vouch.App.Views;

public partial class AccountDetailView : UserControl
{
    public AccountDetailView() => InitializeComponent();

    /// <summary>Persist the account note when the notes box loses focus.</summary>
    private void Notes_LostFocus(object? sender, RoutedEventArgs e)
        => (DataContext as MainViewModel)?.SaveAccountNotes();
}
