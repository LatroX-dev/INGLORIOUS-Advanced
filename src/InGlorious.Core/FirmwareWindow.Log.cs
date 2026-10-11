using System.Windows;

namespace InGlorious.Core;

public partial class FirmwareWindow
{
    private string? _lastLogMessage;

    // Errors may originate in native tools. Never display their raw payload in the wizard.
    private void AppendLog(string raw) => AppendServiceLog(FirmwareMessages.ForUser(raw));

    // Only fixed, translated messages from our service or wizard enter this path.
    private void AppendServiceLog(string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => AppendServiceLog(message)); return; }
        if (_closed || string.IsNullOrWhiteSpace(message) || message == _lastLogMessage) return;
        _lastLogMessage = message;
        if (LogOutput.Text.Length > 16000)
        {
            var nextLine = LogOutput.Text.IndexOf(Environment.NewLine, LogOutput.Text.Length - 12000, StringComparison.Ordinal);
            LogOutput.Text = nextLine < 0 ? "" : LogOutput.Text[(nextLine + Environment.NewLine.Length)..];
        }
        LogOutput.AppendText(DateTime.Now.ToString("HH:mm:ss") + "   " + message + Environment.NewLine + Environment.NewLine);
        LogOutput.ScrollToEnd();
    }

    internal void ShowOfflineProtocolError()
    {
        if (!_offlinePreview) throw new InvalidOperationException("Protocol preview requires offline mode.");
        ShowOfflineStep("connect");
        const string fixture = "#< CLIXML\n<Objs xmlns=\"http://schemas.microsoft.com/powershell/2004/04\"><Obj S=\"progress\"><MS><S>Get-PnpDevice</S></MS></Obj><S S=\"Error\">Exception: Der zuvor identifizierte Bootloader ist nicht eindeutig am bekannten Anschluss vorhanden._x000D__x000A_</S></Objs>";
        StatusText.Text = FirmwareMessages.ForUser(fixture);
        AppendLog(fixture);
        LogExpander.IsExpanded = true;
    }
}
