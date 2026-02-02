using System.IO;
using System.Windows;

namespace SyncBeam.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Ensure SyncBeam directories exist
        var syncBeamDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "SyncBeam");

        Directory.CreateDirectory(Path.Combine(syncBeamDir, "inbox"));
        Directory.CreateDirectory(Path.Combine(syncBeamDir, "outbox"));
    }
}
