// Always-on-top advice window: shows the reader's page in ?overlay mode (route, rewards, events)
// beside the game, so advice is visible without switching windows. The reader must be running.
using Microsoft.Web.WebView2.WinForms;

ApplicationConfiguration.Initialize();
var screen = Screen.PrimaryScreen!.WorkingArea;
var form = new Form
{
    Text = "STS2 建議",
    TopMost = true,
    ShowInTaskbar = true,
    StartPosition = FormStartPosition.Manual,
    Size = new Size(440, 720),
    Location = new Point(screen.Right - 440, screen.Top + 40),
};
var view = new WebView2 { Dock = DockStyle.Fill, Source = new Uri("http://127.0.0.1:8765/?overlay") };
form.Controls.Add(view);
Application.Run(form);
