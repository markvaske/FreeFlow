using System.Windows;
using FreeFlow.Wpf.ViewModels;

namespace FreeFlow.Wpf;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}
