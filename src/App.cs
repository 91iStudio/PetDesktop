using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using static PetDesktop.Native;

namespace PetDesktop;

/// <summary>
/// The app: a pet window, a tray icon with its menu, a small window to type the code from the site.
/// Talks to one address only (see Api), every 12 minutes, and writes down each request in the journal.
/// </summary>
sealed class App
{
    public const string PetClass = "PushishiPet", PairClass = "PushishiPair";
    const uint WM_TRAY = WM_APP + 1, WM_SYNCED = WM_APP + 2, WM_PAIRED = WM_APP + 3;
    const int IdCode = 101, IdButton = 102, IdStatus = 103;

    public static double Scale { get; private set; } = 1;
    static App? _current;

    readonly IntPtr _instance;
    readonly Settings _settings;
    readonly PetWindow _pet;
    IntPtr _pairHwnd, _font, _trayIcon;
    NOTIFYICONDATAW _tray;
    readonly DateTime _started = DateTime.Now;
    bool _syncing;
    PetReply? _lastReply;
    int _lastStatus;
    string? _pendingSheet;
    PairReply? _pairReply;
    int _sentStrokes;
    double _sentSeconds;

    [STAThread]
    static int Main()
    {
        using var single = new Mutex(true, @"Local\Pushishi.91i", out var mine);
        if (!mine) return 0; // already running: one pet is enough
        return new App().Run();
    }

    App()
    {
        _current = this;
        Scale = GetDpiForSystem() / 96.0;
        _instance = GetModuleHandleW(null);
        _settings = Api.LoadSettings();
        Register(PetClass, CS_DBLCLKS, IntPtr.Zero);
        Register(PairClass, 0, (IntPtr)(5 + 1)); // COLOR_WINDOW
        _pet = new PetWindow(this, _instance);
        _font = CreateFontW(-(int)(15 * Scale), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        AddTray();
    }

    unsafe void Register(string name, uint style, IntPtr background)
    {
        var wc = new WNDCLASSEXW
        {
            Size = (uint)sizeof(WNDCLASSEXW), Style = style, WndProc = &Proc, Instance = _instance,
            Cursor = LoadCursorW(IntPtr.Zero, 32512), Background = background, ClassName = Marshal.StringToHGlobalUni(name),
        };
        RegisterClassExW(ref wc);
    }

    [UnmanagedCallersOnly]
    static IntPtr Proc(IntPtr h, uint msg, IntPtr wp, IntPtr lp)
    {
        try
        {
            var app = _current;
            if (app == null) return DefWindowProcW(h, msg, wp, lp);
            if (h == app._pairHwnd) return app.PairProc(h, msg, wp, lp);
            return app._pet?.Handle(h, msg, wp, lp) ?? DefWindowProcW(h, msg, wp, lp);
        }
        catch (Exception e)
        {
            try { File.AppendAllText(Path.Combine(Api.Folder, "errors.txt"), $"{DateTime.Now:u} {e}\n"); } catch { }
            return DefWindowProcW(h, msg, wp, lp);
        }
    }

    int Run()
    {
        LoadCachedPet();
        _pet.StartSync(12 * 60 * 1000);
        if (_settings.Token == null) OpenPair(); else Sync();
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (_pairHwnd != IntPtr.Zero && IsDialogMessageW(_pairHwnd, ref msg)) continue;
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        Shell_NotifyIconW(NIM_DELETE, ref _tray);
        return 0;
    }

    /// <summary>Messages for the pet window that are not about the pet: the tray, finished requests.</summary>
    public IntPtr HandleShared(IntPtr h, uint msg, IntPtr wp, IntPtr lp)
    {
        switch (msg)
        {
            case WM_TRAY:
                var what = (uint)((long)lp & 0xFFFF);
                if (what == WM_RBUTTONUP) ShowMenu();
                else if (what == WM_LBUTTONDBLCLK) OpenSite();
                return IntPtr.Zero;
            case WM_SYNCED: OnSynced(); return IntPtr.Zero;
            case WM_PAIRED: OnPaired(); return IntPtr.Zero;
            case WM_DESTROY: PostQuitMessage(0); return IntPtr.Zero;
        }
        return DefWindowProcW(h, msg, wp, lp);
    }

    // ───── The pet from the site ─────

    void LoadCachedPet()
    {
        if (_settings.SheetFile is { } f && File.Exists(f)) Build(f);
        _pet.Character = _settings.Character ?? "";
        SetTip(_settings.Name != null ? _settings.Name : "Пушиши");
    }

    void Build(string file)
    {
        var sheet = Sprites.LoadPng(file);
        if (sheet == null) return;
        var frames = Sprites.Cut(sheet, _settings.Cols, _settings.Rows);
        var sprites = new Sprites(frames, (int)(_settings.Size * Scale));
        _pet.SetSprites(sprites);
        if (sprites.Face != null) SetIcon(Sprites.Icon(sprites.Face, (int)(16 * Scale) < 20 ? 16 : 32));
        // the big sheet and the unscaled frames are not needed any more: give the memory back right away
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
    }

    /// <summary>Ask the site about the pet (in the background), sending first what happened here since the last time.</summary>
    public void Sync()
    {
        if (_syncing || _settings.Token is not { } token) return;
        _syncing = true;
        int strokes = _pet.Strokes - _sentStrokes, minutes = (int)((_pet.VisibleSeconds - _sentSeconds) / 60);
        Task.Run(async () =>
        {
            try
            {
                if (strokes > 0 || minutes > 0) { await Api.Events(token, strokes, minutes); _sentStrokes += strokes; _sentSeconds += minutes * 60; }
                var (status, reply) = await Api.Pet(token);
                _lastStatus = status; _lastReply = reply;
                if (reply?.Pet?.Sheet is { } url && (url != _settings.SheetUrl || _settings.SheetFile == null || !File.Exists(_settings.SheetFile)))
                    _pendingSheet = await Api.Download(url) is { } file ? url + "\n" + file : null;
            }
            finally { PostMessageW(_pet.Hwnd, WM_SYNCED, IntPtr.Zero, IntPtr.Zero); }
        });
    }

    void OnSynced()
    {
        _syncing = false;
        if (_lastStatus == 401) { _settings.Token = null; Api.SaveSettings(_settings); SetTip("Пушиши — питомец не привязан"); OpenPair(); return; }
        var pet = _lastReply?.Pet;
        if (pet == null) { if (_lastReply?.Message is { } m) SetTip(m); return; }
        _settings.Name = pet.Name; _settings.Cols = pet.Cols; _settings.Rows = pet.Rows; _settings.Character = pet.Look?.Character;
        _pet.Character = _settings.Character ?? "";
        if (_pendingSheet is { } ps)
        {
            var parts = ps.Split('\n');
            _settings.SheetUrl = parts[0]; _settings.SheetFile = parts[1];
            _pendingSheet = null;
            Build(parts[1]);
        }
        Api.SaveSettings(_settings);
        _pet.Sad = pet.Hungry || pet.Bored || pet.Tired;
        _pet.SetAsleep(pet.Asleep);
        _pet.Refresh();
        SetTip(pet.Away ? $"{pet.Name} гостит у бабушки — позовите на сайте"
            : pet.Hungry ? $"{pet.Name} проголодался — покормите на сайте"
            : pet.Bored ? $"{pet.Name} скучает — поиграйте на сайте"
            : pet.Tired ? $"{pet.Name} устал — уложите спать на сайте"
            : pet.Sheet == null ? $"{pet.Name}: портрет ещё рисуется"
            : pet.Name);
    }

    // ───── Tray ─────

    unsafe void AddTray()
    {
        _tray = new NOTIFYICONDATAW { Size = (uint)sizeof(NOTIFYICONDATAW), Hwnd = _pet.Hwnd, Id = 1, Flags = NIF_MESSAGE | NIF_ICON | NIF_TIP, CallbackMessage = WM_TRAY };
        _trayIcon = Sprites.Icon(Sprites.Heart(64), 32);
        _tray.Icon = _trayIcon;
        Write(ref _tray, "Пушиши");
        Shell_NotifyIconW(NIM_ADD, ref _tray);
    }

    static unsafe void Write(ref NOTIFYICONDATAW t, string tip)
    {
        fixed (char* p = t.Tip) { var s = tip.Length > 127 ? tip[..127] : tip; s.AsSpan().CopyTo(new Span<char>(p, 128)); p[s.Length] = '\0'; }
    }

    void SetTip(string tip) { Write(ref _tray, tip); _tray.Flags = NIF_TIP; Shell_NotifyIconW(NIM_MODIFY, ref _tray); }

    void SetIcon(IntPtr icon)
    {
        var old = _trayIcon; _trayIcon = icon;
        _tray.Icon = icon; _tray.Flags = NIF_ICON; Shell_NotifyIconW(NIM_MODIFY, ref _tray);
        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    public void ShowMenu()
    {
        var m = CreatePopupMenu();
        var sizes = CreatePopupMenu();
        foreach (var (px, label) in new[] { (48, "Крошечный"), (64, "Обычный"), (96, "Большой"), (128, "Огромный") })
            AppendMenuW(sizes, MF_STRING | (_settings.Size == px ? MF_CHECKED : 0), 10 + px, label);
        AppendMenuW(m, MF_STRING, 1, "Открыть сайт питомца");
        AppendMenuW(m, MF_POPUP, sizes, "Размер");
        AppendMenuW(m, MF_STRING, 2, _pet.HiddenByUser ? "Показать питомца" : "Спрятать на час");
        AppendMenuW(m, MF_STRING | (Autostart ? MF_CHECKED : 0), 3, "Запускать вместе с Windows");
        AppendMenuW(m, MF_SEPARATOR, 0, null);
        AppendMenuW(m, MF_STRING, 4, _settings.Token == null ? "Привязать питомца…" : "Привязать другого питомца…");
        AppendMenuW(m, MF_STRING, 5, "Что я отправлял");
        AppendMenuW(m, MF_STRING, 6, "Нагрузка на компьютер");
        AppendMenuW(m, MF_SEPARATOR, 0, null);
        AppendMenuW(m, MF_STRING, 9, "Выход");
        GetCursorPos(out var p);
        SetForegroundWindow(_pet.Hwnd);
        var cmd = TrackPopupMenu(m, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY, p.X, p.Y, 0, _pet.Hwnd, IntPtr.Zero);
        DestroyMenu(m);
        switch (cmd)
        {
            case 1: OpenSite(); break;
            case 2: _pet.HideForHour(!_pet.HiddenByUser); break;
            case 3: Autostart = !Autostart; break;
            case 4: OpenPair(); break;
            case 5: OpenJournal(); break;
            case 6: ShowLoad(); break;
            case 9: DestroyWindow(_pet.Hwnd); break;
            case >= 58 and <= 138:
                _settings.Size = cmd - 10; Api.SaveSettings(_settings);
                if (_settings.SheetFile is { } f && File.Exists(f)) Build(f);
                break;
        }
    }

    static void OpenSite() => ShellExecuteW(IntPtr.Zero, "open", Api.SiteUrl, null, null, SW_SHOW);

    static void OpenJournal()
    {
        Directory.CreateDirectory(Api.Folder);
        if (!File.Exists(Api.JournalFile)) File.WriteAllText(Api.JournalFile, "Пока ничего не отправлялось.\r\n");
        ShellExecuteW(IntPtr.Zero, "open", "notepad.exe", $"\"{Api.JournalFile}\"", null, SW_SHOW);
    }

    void ShowLoad()
    {
        var p = Process.GetCurrentProcess();
        var up = DateTime.Now - _started;
        var cpu = p.TotalProcessorTime.TotalSeconds;
        MessageBoxW(_pet.Hwnd,
            $"Память: {p.WorkingSet64 / 1048576.0:0.0} МБ\n" +
            $"Процессор: {cpu:0.0} с за {up.TotalMinutes:0} мин работы ({cpu / Math.Max(1, up.TotalSeconds) * 100:0.00}% в среднем)\n" +
            $"Перерисовок питомца: {_pet.Redraws}\n\n" +
            "Когда питомец спит или на экране игра во весь экран, он не перерисовывается вовсе.",
            "Пушиши — нагрузка", 0x40);
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static bool Autostart
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("Pushishi") is string; }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue("Pushishi", $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue("Pushishi", false);
        }
    }

    // ───── Pairing: the code from the site ─────

    void OpenPair()
    {
        if (_pairHwnd != IntPtr.Zero) { SetForegroundWindow(_pairHwnd); return; }
        int W = (int)(420 * Scale), H = (int)(250 * Scale);
        GetCursorPos(out _);
        SystemParametersInfoW(SPI_GETWORKAREA, 0, out var work, 0);
        _pairHwnd = CreateWindowExW(0, PairClass, "Пушиши — привязать питомца", WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_VISIBLE,
            (work.Left + work.Right - W) / 2, (work.Top + work.Bottom - H) / 2, W, H, IntPtr.Zero, IntPtr.Zero, _instance, IntPtr.Zero);
        int pad = (int)(18 * Scale), cw = W - pad * 2 - (int)(16 * Scale);
        Child("STATIC", "Откройте сайт питомца, в меню выберите «Питомец на рабочем столе» и введите код, который там увидите:",
            0, pad, pad, cw, (int)(60 * Scale), 0);
        var edit = Child("EDIT", "", WS_TABSTOP | WS_BORDER | ES_CENTER | ES_NUMBER, pad, pad + (int)(70 * Scale), (int)(160 * Scale), (int)(32 * Scale), IdCode);
        SendMessageW(edit, EM_LIMITTEXT, 6, IntPtr.Zero);
        Child("BUTTON", "Привязать", WS_TABSTOP | BS_DEFPUSHBUTTON, pad + (int)(176 * Scale), pad + (int)(69 * Scale), (int)(140 * Scale), (int)(34 * Scale), IdButton);
        Child("STATIC", "", 0, pad, pad + (int)(116 * Scale), cw, (int)(44 * Scale), IdStatus);
        SetFocus(edit);
    }

    IntPtr Child(string cls, string text, uint style, int x, int y, int w, int h, int id)
    {
        var c = CreateWindowExW(0, cls, text, WS_CHILD | WS_VISIBLE | style, x, y, w, h, _pairHwnd, id, _instance, IntPtr.Zero);
        SendMessageW(c, WM_SETFONT, _font, 1);
        return c;
    }

    unsafe IntPtr PairProc(IntPtr h, uint msg, IntPtr wp, IntPtr lp)
    {
        switch (msg)
        {
            case WM_COMMAND when ((int)wp & 0xFFFF) is IdButton or 1:
                var buf = stackalloc char[16];
                var n = GetWindowTextW(GetDlgItem(h, IdCode), buf, 16);
                var code = new string(buf, 0, n).Trim();
                if (code.Length != 6) { SetWindowTextW(GetDlgItem(h, IdStatus), "Код — это 6 цифр."); return IntPtr.Zero; }
                SetWindowTextW(GetDlgItem(h, IdStatus), "Проверяю…");
                StartPair(code);
                return IntPtr.Zero;
            case WM_CTLCOLORSTATIC: return GetSysColorBrush(5);
            case WM_CLOSE: DestroyWindow(h); _pairHwnd = IntPtr.Zero; return IntPtr.Zero;
        }
        return DefWindowProcW(h, msg, wp, lp);
    }

    void StartPair(string code) =>
        Task.Run(async () => { _pairReply = await Api.Pair(code); PostMessageW(_pet.Hwnd, WM_PAIRED, IntPtr.Zero, IntPtr.Zero); });

    void OnPaired()
    {
        var r = _pairReply;
        if (r?.Token is { Length: > 0 } token)
        {
            _settings.Token = token; _settings.SheetUrl = null; Api.SaveSettings(_settings);
            if (_pairHwnd != IntPtr.Zero) { DestroyWindow(_pairHwnd); _pairHwnd = IntPtr.Zero; }
            Sync();
            return;
        }
        if (_pairHwnd != IntPtr.Zero) SetWindowTextW(GetDlgItem(_pairHwnd, IdStatus), r?.Error ?? "Не получилось. Попробуйте ещё раз.");
    }
}
