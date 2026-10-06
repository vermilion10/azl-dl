using Avalonia.Controls;
using AzlDl.App.ViewModels;

namespace AzlDl.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (DataContext is MainViewModel main)
            {
                await main.Ships.LoadAsync();
            }
        };
    }
}
