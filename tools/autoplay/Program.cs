// Mod-free combat autoplayer: reads /capture and /solve from the running reader (127.0.0.1:8765) and
// plays the first step of the solver's route with simulated mouse/keyboard input, then repeats.
// Cards and enemies are chosen with the keyboard (1..0 card, arrows, Space) and checked through the reader's /hover.
// Stop at any time with F8. Usage: dotnet run --project tools/autoplay -c Release
using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;

static class Autoplay
{
    static readonly HttpClient Http = new() { BaseAddress = new Uri("http://127.0.0.1:8765"), Timeout = TimeSpan.FromSeconds(90) };

    static int Main()
    {
        SetProcessDpiAwarenessContext(-4);
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("自動出牌啟動：只在戰鬥中、輪到玩家時動作。按 F8 停止。");
        string? lastState = null;
        int stuck = 0;
        while (!StopRequested())
        {
            JsonElement capture;
            try { capture = Get("/capture"); }
            catch (Exception e) { Console.WriteLine($"讀取器沒有回應：{e.Message}"); Sleep(3000); continue; }
            if (capture.TryGetProperty("error", out var err)) { Status(err.GetString()); Sleep(2000); continue; }

            string state = StateKey(capture);
            if (state == lastState && ++stuck >= 3) { Console.WriteLine("同一狀態連續三次沒有變化，停止以免誤操作。"); return 1; }
            if (state != lastState) stuck = 0;
            lastState = state;

            JsonElement solve = Get("/solve");
            if (solve.TryGetProperty("error", out var serr)) { Status($"求解失敗：{serr.GetString()}"); Sleep(3000); continue; }
            if (!solve.TryGetProperty("route", out var route) || route.GetArrayLength() == 0) { Status("求解器沒有給出路線。"); Sleep(3000); continue; }
            JsonElement step = route[0];
            if (!FocusGame(out var window)) { Console.WriteLine("找不到遊戲視窗。"); Sleep(3000); continue; }

            string kind = step.GetProperty("kind").GetString()!;
            Console.WriteLine($"{kind} {Describe(step)}");
            switch (kind)
            {
                case "end":
                    Key(0x45); // E ends the turn
                    Sleep(5000);
                    break;
                case "potion":
                    int slot = step.GetProperty("potionSlot").GetInt32();
                    Click(window, 526 + slot * 63, 40); Sleep(500);
                    Click(window, 526 + slot * 63, 187); Sleep(800);
                    if (TargetIndex(step) is int pk) { if (AimAt(pk)) Key(0x20); else Key(0x1B); Sleep(800); }
                    break;
                case "play":
                    if (step.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
                    {
                        Console.WriteLine("這一步需要選牌，自動出牌尚未支援；請手動完成，完成後會繼續。");
                        WaitForChange(capture); continue;
                    }
                    int index = HandIndex(capture, step);
                    if (index < 0) { Console.WriteLine("手牌中找不到這張牌，重新讀取。"); Sleep(1500); continue; }
                    Click(window, 960, 250); Sleep(150); // empty backdrop: gives the game keyboard focus
                    Key((byte)(index == 9 ? 0x30 : 0x31 + index)); // 1..0 select hand card
                    Sleep(450);
                    if (TargetIndex(step) is int k && !AimAt(k)) { Key(0x1B); Console.WriteLine("無法用方向鍵選到目標，已取消。"); Sleep(1000); continue; }
                    Key(0x20); // Space (ui_select) confirms the play
                    Sleep(1500);
                    break;
            }
        }
        Console.WriteLine("已停止。");
        return 0;
    }

    static JsonElement Get(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-STS2-Capture", "1");
        using var response = Http.Send(request);
        return JsonDocument.Parse(response.Content.ReadAsStream()).RootElement.Clone();
    }

    static string StateKey(JsonElement capture)
    {
        var p = capture.GetProperty("players")[0];
        return $"{capture.GetProperty("round")}|{p.GetProperty("energy")}|{p.GetProperty("hand")}|{capture.GetProperty("enemies")}";
    }

    static void WaitForChange(JsonElement before)
    {
        string key = StateKey(before);
        while (!StopRequested())
        {
            Sleep(1500);
            try { var now = Get("/capture"); if (now.TryGetProperty("error", out _) || StateKey(now) != key) return; }
            catch { return; }
        }
    }

    static string Describe(JsonElement step) =>
        string.Join(" ", new[] { "card", "potion", "target" }.Select(k => step.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null).Where(s => !string.IsNullOrEmpty(s)));

    static string Normalize(string s) => s.Replace("_", "").ToLowerInvariant();

    static int HandIndex(JsonElement capture, JsonElement step)
    {
        string card = Normalize(step.GetProperty("card").GetString()!);
        bool upgraded = step.GetProperty("upgrade").GetInt32() > 0;
        var hand = capture.GetProperty("players")[0].GetProperty("hand");
        for (int i = 0; i < hand.GetArrayLength(); i++)
        {
            var c = hand[i];
            bool up = c.TryGetProperty("upgrades", out var u) && u.ValueKind == JsonValueKind.Number && u.GetInt32() > 0;
            if (Normalize(c.GetProperty("name").GetString()!) == card && up == upgraded) return i;
        }
        return -1;
    }

    static int? TargetIndex(JsonElement step)
    {
        if (!step.TryGetProperty("target", out var t) || t.ValueKind != JsonValueKind.String) return null;
        string target = t.GetString()!;
        int hash = target.LastIndexOf('#');
        return hash >= 0 && int.TryParse(target[(hash + 1)..], out int k) ? k : null;
    }

    // Keyboard targeting starts on the last-targeted (or first) enemy; arrows move by screen position,
    // so step toward the wanted roster index and re-read the focused enemy after every press.
    static bool AimAt(int k)
    {
        for (int tries = 0; tries < 8; tries++)
        {
            int hovered = Get("/hover").GetProperty("hovered").GetInt32();
            if (hovered == k) return true;
            Key((byte)(hovered == 0 || hovered < k ? 0x27 : 0x25)); // Right / Left
            Sleep(300);
        }
        return false;
    }

    static void Status(string? text) { Console.Write($"\r{text}".PadRight(60)); }

    static bool StopRequested() => (GetAsyncKeyState(0x77) & 0x8000) != 0; // F8

    static void Sleep(int ms)
    {
        for (int t = 0; t < ms && !StopRequested(); t += 100) Thread.Sleep(100);
        if (StopRequested()) { Console.WriteLine("\n已停止（F8）。"); Environment.Exit(0); }
    }

    // ---- Window and input ----
    static bool FocusGame(out IntPtr window)
    {
        window = Process.GetProcessesByName("SlayTheSpire2").FirstOrDefault()?.MainWindowHandle ?? IntPtr.Zero;
        if (window == IntPtr.Zero) return false;
        SetForegroundWindow(window);
        return true;
    }

    // Design (1920x1080, letterboxed) -> screen pixels.
    static (int, int) ToScreen(IntPtr window, int x, int y)
    {
        GetClientRect(window, out RECT r);
        double scale = Math.Min(r.Right / 1920.0, r.Bottom / 1080.0);
        double ox = (r.Right - 1920 * scale) / 2, oy = (r.Bottom - 1080 * scale) / 2;
        var p = new POINT { X = (int)(ox + x * scale), Y = (int)(oy + y * scale) };
        ClientToScreen(window, ref p);
        return (p.X, p.Y);
    }

    static void Click(IntPtr window, int x, int y)
    {
        var (sx, sy) = ToScreen(window, x, y);
        SetCursorPos(sx, sy); Thread.Sleep(60);
        mouse_event(0x02, 0, 0, 0, 0); Thread.Sleep(60); mouse_event(0x04, 0, 0, 0, 0);
    }

    static void Key(byte vk) { keybd_event(vk, 0, 0, 0); Thread.Sleep(50); keybd_event(vk, 0, 2, 0); }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(nint value);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, int data, int extra);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, int extra);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
}
