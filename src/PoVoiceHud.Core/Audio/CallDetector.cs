using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace PoVoiceHud.Core.Audio;

/// <summary>
/// Whether a call is going on: some call app (Teams, Zoom, Slack, Webex, Discord, a browser for Meet…) has a microphone
/// open right now. Windows keeps an audio session per app on every capture device; an active one is an app recording.
/// Nothing is opened or listened to to find out.
/// </summary>
public static class CallDetector
{
    // Process names, and what to call them.
    private static readonly (string Process, string Name)[] CallApps =
    [
        ("ms-teams", "Teams"), ("teams", "Teams"), ("zoom", "Zoom"), ("slack", "Slack"), ("webex", "Webex"), ("ciscocollabhost", "Webex"),
        ("discord", "Discord"), ("skype", "Skype"), ("ringcentral", "RingCentral"), ("g2mcomm", "GoTo Meeting"), ("goto", "GoTo"),
        ("whatsapp", "WhatsApp"), ("signal", "Signal"), ("telegram", "Telegram"), ("chrome", "Chrome"), ("msedge", "Edge"),
        ("firefox", "Firefox"), ("brave", "Brave"),
    ];

    /// <summary>The call app recording now ("Teams"), or null when there is none. This app itself never counts.</summary>
    public static string? CurrentCall()
    {
        var self = Environment.ProcessId;
        var recording = new List<string>();
        try
        {
            using var devices = new MMDeviceEnumerator();
            foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                {
                    var sessions = device.AudioSessionManager;
                    sessions.RefreshSessions();
                    for (var i = 0; i < sessions.Sessions.Count; i++)
                    {
                        using var session = sessions.Sessions[i];
                        var pid = (int)session.GetProcessID;
                        if (session.State == AudioSessionState.AudioSessionStateActive && pid != 0 && pid != self && ProcessName(pid) is { } name)
                            recording.Add(name);
                    }
                }
            }
        }
        catch (Exception e) when (e is COMException or InvalidOperationException)
        {
            return null;   // no capture device, or the audio service is restarting
        }
        return Find(recording);
    }

    /// <summary>The first call app among the processes recording now, by their names ("ms-teams", "Zoom").</summary>
    public static string? Find(IEnumerable<string> recording) =>
        recording.Select(process => CallApps.FirstOrDefault(c => process.StartsWith(c.Process, StringComparison.OrdinalIgnoreCase)).Name)
            .FirstOrDefault(name => name is not null);

    private static string? ProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return null;   // ended since
        }
    }
}
