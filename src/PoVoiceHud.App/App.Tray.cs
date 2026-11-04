using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using PoVoiceHud.Core;
using PoVoiceHud.Core.Code;
using PoVoiceHud.Core.Config;
using PoVoiceHud.Core.Diagnostics;
using PoVoiceHud.Core.Meetings;
using PoVoiceHud.Core.Transcription;
using Windows.ApplicationModel.DataTransfer;

namespace PoVoiceHud.App;

/// <summary>The tray icon's menu and what it runs: show and hide, the history folder, diagnostics, updates.</summary>
public partial class App
{
    private async Task CheckUpdatesAsync(bool userAsked)
    {
        var message = await Updates.CheckAsync(_settings.UpdateFeed, _log, userAsked);
        if (message is not null) _window?.ViewModel.SetStatus(message);
    }

    /// <summary>The tray menu: built each time it opens so its labels match the current state.</summary>
    private MenuFlyout TrayMenu()
    {
        static MenuFlyoutItem Item(string text, Action run)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => run();
            return item;
        }
        var menu = new MenuFlyout();
        var viewModel = _window!.ViewModel;
        menu.Items.Add(Item(_window.AppWindow.IsVisible ? "Hide the HUD" : "Show the HUD", ToggleVisible));
        if (viewModel.ShowsPages) menu.Items.Add(Item(viewModel.IsMuted ? "Unmute" : "Mute", () => viewModel.ToggleMuteCommand.Execute(null)));
        menu.Items.Add(Item("Open the history folder", () => OpenFolder(Sessions.Directory)));
        menu.Items.Add(Item("Copy diagnostics", () => _ = CollectDiagnosticsAsync()));
        menu.Items.Add(Item("Check for updates", () => _ = CheckUpdatesAsync(userAsked: true)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Quit PoVoiceHud", _window.Close));
        return menu;
    }

    private void ToggleVisible()
    {
        if (_window!.AppWindow.IsVisible) _window.AppWindow.Hide();
        else _window.AppWindow.Show();
    }

    private static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true })?.Dispose();
    }

    /// <summary>Zips logs and settings into %TEMP%, puts the zip on the clipboard and shows it in Explorer.</summary>
    private async Task CollectDiagnosticsAsync()
    {
        var zip = Path.Combine(Path.GetTempPath(), $"PoVoiceHud-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        var settings = _settings;
        var environment = string.Join("\n",
            $"PoVoiceHud {Version} ({(IsInstalled() ? "installed" : "not installed: run from a build folder")})",
            $"Windows {Environment.OSVersion.Version} · {RuntimeInformation.FrameworkDescription} · {Environment.ProcessorCount} logical cores",
            $"Audio: microphone and call · model: {settings.ModelMode}, {ServiceCollectionExtensions.Describe(settings)}" +
            $" · key stored: {(CredentialStore.Read(CredentialStore.FoundryKey) is null ? "no" : "yes")}",
            $"Whisper: {settings.WhisperModel} on the {WhisperTranscriber.Runtime} (graphics card {(settings.UseGpu ? "allowed" : "off")}) · voices told apart: {(settings.SeparateSpeakers ? "on" : "off")}" +
            $" · share webhook: {(Webhook() is { } hook ? SummaryShare.NameOf(hook) : "none")}",
            $"Wikipedia: {(settings.LookUpClaims ? "on" : "off")} · repository: {(settings.Repository is null ? "none" : RepoSource.IsRemote(settings.Repository) ? "URL" : "folder")}" +
            $" · repository token stored: {(CredentialStore.Read(CredentialStore.RepoToken) is null ? "no" : "yes")} · start with Windows: {(settings.StartWithWindows ? "on" : "off")}" +
            $" · update feed: {(settings.UpdateFeed is null ? "none" : "set")}",
            "");
        try
        {
            await Task.Run(() => DiagnosticsBundle.Create(AppPaths.DataDir, zip, environment));
            var data = new DataPackage();
            data.SetStorageItems([await Windows.Storage.StorageFile.GetFileFromPathAsync(zip)]);
            Clipboard.SetContent(data);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{zip}\"") { UseShellExecute = true })?.Dispose();
            _log.LogInformation("Diagnostics written to {Zip}", zip);
            _window?.ViewModel.SetStatus($"{Path.GetFileName(zip)} is on the clipboard");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _window?.ViewModel.SetStatus($"Diagnostics failed: {e.Message}");
        }
    }

    private static bool IsInstalled()
    {
        try
        {
            return new Velopack.UpdateManager(AppContext.BaseDirectory).IsInstalled;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or Velopack.Exceptions.NotInstalledException)
        {
            return false;
        }
    }
}
