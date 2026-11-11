using System.Windows;
using System.Windows.Controls;
namespace MonitorRFC.Agent.Manager;
public partial class MainWindow : Window
{
    private readonly ManagerViewModel model = new();
    public MainWindow()
    {
        InitializeComponent(); DataContext = model;
        model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ManagerViewModel.HtmlPreview)) PreviewBrowser.NavigateToString(model.HtmlPreview); };
        PreviewBrowser.NavigateToString(model.HtmlPreview);
    }
    private void SqlChanged(object sender, RoutedEventArgs e) => model.SqlPassword = ((PasswordBox)sender).Password;
    private void SmtpChanged(object sender, RoutedEventArgs e) => model.NewPassword = ((PasswordBox)sender).Password;
}
