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
    private async void DefaultEmailClicked(object sender, RoutedEventArgs e) { model.NotifyActionStarted("Cargando plantilla predeterminada"); await model.ExecuteAsync("default-email"); }
    private async void PreviewEmailClicked(object sender, RoutedEventArgs e) { model.NotifyActionStarted("Generando vista previa"); await model.ExecuteAsync("preview"); }
    private async void SaveEmailClicked(object sender, RoutedEventArgs e) { model.NotifyActionStarted("Guardando configuración"); await model.ExecuteAsync("save"); }
    private async void TestEmailClicked(object sender, RoutedEventArgs e) { model.NotifyActionStarted("Enviando correo de prueba"); await model.ExecuteAsync("test-email"); }
}
