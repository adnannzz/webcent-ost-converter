using System.Windows;
using System.Windows.Controls;
using OstConverter.App.ViewModels;

namespace OstConverter.App.Views;

public partial class OptionsView : UserControl
{
    public OptionsView() => InitializeComponent();

    void OnFolderSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is OptionsViewModel vm) vm.SelectedFolder = e.NewValue as FolderNode;
    }
}
