namespace InGlorious.Core;

public partial class MainWindow
{
    private static string DeviceMetadataReadFailureText() => L.T(
        "Die Tastatur konnte gerade nicht gelesen werden. Bitte versuche es erneut. Dein Profil bleibt unverändert.");

    private void ShowDeviceMetadataReadFailure()
    {
        var message = DeviceMetadataReadFailureText();
        Status(message);
        UnsavedChangesWindow.ShowInformation(this, L.T("Von Tastatur laden"), message);
    }
}
