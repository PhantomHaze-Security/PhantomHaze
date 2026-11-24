namespace PhantomHaze;

// The top bar's styles (Settings -> Appearance -> Top bar).
public partial class MainForm
{
    /// <summary>How the top bar looks. Stored in settings.json by name.</summary>
    private enum TopBarStyle
    {
        /// <summary>The full top bar (the default).</summary>
        Comfortable,

        /// <summary>Only the Log View and Web View buttons and extension buttons; the rest opens from a drop-down button.</summary>
        Compact,

        /// <summary>Everything, closer together.</summary>
        Comfy,

        /// <summary>An ON/OFF protection switch and only the essential Web View controls.</summary>
        Simple,
    }
}
