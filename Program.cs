namespace PhantomHaze;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhantomHaze");
        PhantomUser? requested = null;
        string? sessionLog = null;
        try
        {
            var store = new PhantomUserStore(root);
            do
            {
                var session = store.Open(requested);
                using (session.Lease)
                using (var window = new MainForm(store, session.User, session.Lease, sessionLog))
                {
                    Application.Run(window);
                    requested = window.NextUser;
                    sessionLog = window.LogForNextUser;
                }
                RenderedPageSessionData.ClearAll();
                store.FinishSession();
            } while (requested is not null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            MessageBox.Show(ex.Message, "PhantomHaze Users", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
