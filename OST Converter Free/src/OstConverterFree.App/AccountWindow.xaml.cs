using System.Windows;
using OstConverter.App.ViewModels;

namespace OstConverter.App;

public partial class AccountWindow : Window
{
    public AccountWindow(AccountViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Close();
    }

    void OnClose(object sender, RoutedEventArgs e) => Close();
}
