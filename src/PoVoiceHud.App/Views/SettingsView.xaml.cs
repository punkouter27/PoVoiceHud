using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PoVoiceHud.Core.ViewModels;

namespace PoVoiceHud.App.Views;

public sealed partial class SettingsView : UserControl
{
    public SettingsView(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }

    /// <summary>Closes the app; set by the window.</summary>
    public Action? Quit { get; set; }

    public static string KeyPlaceholder(bool hasKey) => hasKey ? "•••••••• (stored — type to replace)" : "not set";

    public static string TokenPlaceholder(bool hasToken) => hasToken ? "•••••••• (stored — type to replace)" : "only for a private repository";

    public static string WebhookPlaceholder(bool hasWebhook) => hasWebhook ? "•••••••• (stored — type to replace)" : "https://hooks.slack.com/services/…, a Teams workflow URL, or Discord's";

    /// <summary>A line that takes no room until there is something to say.</summary>
    public static Visibility Shown(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The connection's dot: green once the check passed, red when it failed, grey while checking.</summary>
    public static Brush Dot(string? result) => Theme(result switch
    {
        ['✓', ..] => "SystemFillColorSuccessBrush",
        ['✗', ..] => "SystemFillColorCriticalBrush",
        _ => "ControlStrongFillColorDisabledBrush",
    });

    private static Brush Theme(string key) => (Brush)Application.Current.Resources[key];

    private void Quit_Click(object sender, RoutedEventArgs e) => Quit?.Invoke();
}
