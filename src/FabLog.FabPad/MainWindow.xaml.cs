using System.Windows;

namespace FabLog.FabPad;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Web.Services = App.Services;
    }
}
