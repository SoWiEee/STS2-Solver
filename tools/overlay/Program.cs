// Always-on-top advice window: shows the reader's page in ?overlay mode (route, rewards, events)
// beside the game, so advice is visible without switching windows. The reader must be running.
using Microsoft.Web.WebView2.WinForms;

static class Overlay
{
    [STAThread] // WebView2 needs a single-threaded apartment (RPC_E_CHANGED_MODE otherwise)
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        var screen = Screen.PrimaryScreen!.WorkingArea;
        // Default: left edge below the HP bar, clear of the game's right-side buttons (end turn, proceed).
        var bounds = new Rectangle(screen.Left, screen.Top + 110, 380, 560);
        string saved = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sts2Overlay.txt");
        try
        {
            int[] v = File.ReadAllText(saved).Split(',').Select(int.Parse).ToArray();
            var r = new Rectangle(v[0], v[1], v[2], v[3]);
            if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r))) bounds = r;
        }
        catch (Exception e) when (e is IOException or FormatException or IndexOutOfRangeException) { }
        var form = new Form
        {
            Text = "STS2 建議",
            TopMost = true,
            StartPosition = FormStartPosition.Manual,
            Bounds = bounds,
        };
        // Remember where the player moved or resized it.
        form.FormClosing += (_, _) =>
        {
            var b = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
            File.WriteAllText(saved, $"{b.X},{b.Y},{b.Width},{b.Height}");
        };
        var view = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(view);
        view.CoreWebView2InitializationCompleted += (_, e) =>
        {
            if (e.IsSuccess) view.CoreWebView2.Navigate("http://127.0.0.1:8765/?overlay");
            else form.Text = $"STS2 建議 · WebView2 無法啟動：{e.InitializationException?.Message}";
        };
        form.Load += async (_, _) => await view.EnsureCoreWebView2Async();
        Application.Run(form);
    }
}
