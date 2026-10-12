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
    int _sentStrokes, _sentWarm;
    string? _eggKey;
    int _eggMinutesLeft = -1;
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
            case WM_PAIRED: OnPaired(wp != IntPtr.Zero); return IntPtr.Zero;
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
        int strokes = _pet.Strokes - _sentStrokes, minutes = (int)((_pet.VisibleSeconds - _sentSeconds) / 60), warm = _pet.WarmTaps - _sentWarm;
        Task.Run(async () =>
        {
            try
            {
                if (strokes > 0 || minutes > 0 || warm > 0) { await Api.Events(token, strokes, minutes, warm); _sentStrokes += strokes; _sentSeconds += minutes * 60; _sentWarm += warm; }
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
        if (pet == null && _lastReply?.Egg is { } egg) { ShowEgg(egg); return; }
        if (pet == null) { if (_lastReply?.Message is { } m) SetTip(m); return; }
        if (pet.Sheet == null && _pet.IsEgg) { SetTip($"{pet.Name} вылупился! Рисуем его портрет — скоро выйдет гулять"); return; }
        if (_pet.IsEgg) { _pet.IsEgg = false; _eggKey = null; }
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

    /// <summary>The egg on the taskbar: drawn in the baby's colours; strokes warm it.</summary>
    DeskEgg? _lastEgg;

    void ShowEgg(DeskEgg egg)
    {
        _lastEgg = egg;
        var look = egg.Look ?? new DeskLook();
        var key = $"{egg.Id}|{look.Base}|{look.Mark}|{look.Pattern}|{_settings.Size}";
        if (key != _eggKey)
        {
            _eggKey = key;
            uint b = Sprites.Hex(look.Base, 0xFFF4E3C3), m = Sprites.Hex(look.Mark, 0xFFB9A3E3);
            int h = (int)(_settings.Size * Scale * 0.9);
            Pixels Tilt(double d) => Sprites.Egg(h, b, m, look.Pattern, look.Rare, d);
            Pixels up = Tilt(0), left = Tilt(-7), right = Tilt(7);
            var frames = new List<Pixels> { up, up, left, right, up, right, up, up, up, up, up, up };
            _pet.IsEgg = true;
            _pet.SetSprites(new Sprites(frames, h));
            if (Sprites.Icon(Sprites.PetFace(64), 32) is var icon) SetIcon(icon);
        }
        _eggMinutesLeft = egg.MinutesLeft;
        var left2 = egg.MinutesLeft;
        SetTip(left2 <= 0 ? "Тук-тук! Малыш стучится — откройте сайт, чтобы он вылупился (двойной щелчок по яйцу)"
            : $"Яйцо: малыш вылупится через {(left2 >= 60 ? $"{left2 / 60} ч " : "")}{left2 % 60} мин." + (egg.WarmLeft > 0 ? " Погладьте яйцо мышкой — согреете!" : ""));
    }

    public void EggDoubleClick() => OpenSite();

    // ───── Tray ─────

    unsafe void AddTray()
    {
        _tray = new NOTIFYICONDATAW { Size = (uint)sizeof(NOTIFYICONDATAW), Hwnd = _pet.Hwnd, Id = 1, Flags = NIF_MESSAGE | NIF_ICON | NIF_TIP, CallbackMessage = WM_TRAY };
        _trayIcon = Sprites.Icon(Sprites.PetFace(64), 32);
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
        if (_settings.Token != null) AppendMenuW(m, MF_STRING, 7, "Спросить сайт сейчас");
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
            case 7: Sync(); break; // woken or fed on the site — no need to wait for the 12-minute check
            case 9: DestroyWindow(_pet.Hwnd); break;
            case >= 58 and <= 138:
                _settings.Size = cmd - 10; Api.SaveSettings(_settings);
                if (_pet.IsEgg && _lastEgg != null) { _eggKey = null; ShowEgg(_lastEgg); } // the egg is redrawn at the new size right away
                else if (_settings.SheetFile is { } f && File.Exists(f)) Build(f);
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

    // ───── Pairing: one click on the site (or the code from its menu) ─────

    const int IdOpen = 104, IdTitle = 105, IdSub = 106, IdOr = 107, TimerPoll = 7;
    IntPtr _titleFont, _bigFont, _codeFont, _faceBmp, _white;
    bool _dark;
    readonly IntPtr _editDark = CreateSolidBrush(0x2B2B2B);

    /// <summary>Windows in dark mode (apps): the same setting the system's own windows follow.</summary>
    static bool SystemDark()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }
    int _faceSize;
    string? _nonce;
    DateTime _pollUntil;

    int PairW => (int)(470 * Scale);

    void OpenPair()
    {
        if (_pairHwnd != IntPtr.Zero) { SetForegroundWindow(_pairHwnd); return; }
        int W = PairW, H = (int)(624 * Scale);
        SystemParametersInfoW(SPI_GETWORKAREA, 0, out var work, 0);
        _pairHwnd = CreateWindowExW(0, PairClass, "Пушиши", WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_VISIBLE,
            (work.Left + work.Right - W) / 2, (work.Top + work.Bottom - H) / 2, W, H, IntPtr.Zero, IntPtr.Zero, _instance, IntPtr.Zero);
        if (_titleFont == IntPtr.Zero) _titleFont = CreateFontW(-(int)(24 * Scale), 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        if (_bigFont == IntPtr.Zero) _bigFont = CreateFontW(-(int)(16 * Scale), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        if (_codeFont == IntPtr.Zero) _codeFont = CreateFontW(-(int)(26 * Scale), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _dark = SystemDark();
        if (_white != IntPtr.Zero) DeleteObject(_white);
        _white = CreateSolidBrush(_dark ? 0x202020u : 0xFFFFFFu); // the window's own background (BGR)
        if (_dark) { var on = 1; DwmSetWindowAttribute(_pairHwnd, 20, ref on, 4); } // dark title bar
        _faceSize = (int)(132 * Scale);
        if (_faceBmp == IntPtr.Zero) _faceBmp = Sprites.Dib(WelcomePicture(_faceSize));
        var icon = Sprites.Icon(Sprites.PetFace(64), 32);
        SendMessageW(_pairHwnd, WM_SETICON, 1, icon); SendMessageW(_pairHwnd, WM_SETICON, 0, icon);

        int pad = (int)(28 * Scale), cw = W - pad * 2 - (int)(16 * Scale), y = (int)(20 * Scale) + _faceSize + (int)(12 * Scale);
        SendMessageW(Child("STATIC", "Привяжите питомца", SS_CENTER, pad, y, cw, (int)(34 * Scale), IdTitle), WM_SETFONT, _titleFont, 1);
        y += (int)(40 * Scale);
        Child("STATIC", "Он живёт на сайте pet.91i.ru. Привяжите его — и он придёт гулять по панели задач.", SS_CENTER, pad, y, cw, (int)(42 * Scale), IdSub);
        y += (int)(56 * Scale);
        var open = Child("BUTTON", "Открыть сайт и привязать", WS_TABSTOP, pad + (int)(30 * Scale), y, cw - (int)(60 * Scale), (int)(48 * Scale), IdOpen);
        SendMessageW(open, WM_SETFONT, _bigFont, 1);
        y += (int)(66 * Scale);
        Child("STATIC", "или введите код с сайта:\nменю ☰ → «Питомец на рабочем столе»", SS_CENTER, pad, y, cw, (int)(42 * Scale), IdOr);
        y += (int)(50 * Scale);
        int ew = (int)(170 * Scale), bw = (int)(120 * Scale), gap = (int)(10 * Scale), x0 = pad + (cw - ew - bw - gap) / 2;
        var edit = Child("EDIT", "", WS_TABSTOP | WS_BORDER | ES_CENTER | ES_NUMBER, x0, y, ew, (int)(44 * Scale), IdCode);
        SendMessageW(edit, WM_SETFONT, _codeFont, 1);
        SendMessageW(edit, EM_LIMITTEXT, 6, IntPtr.Zero);
        Child("BUTTON", "Привязать", WS_TABSTOP, x0 + ew + gap, y + (int)(4 * Scale), bw, (int)(36 * Scale), IdButton);
        y += (int)(56 * Scale);
        Child("STATIC", "", SS_CENTER, pad, y, cw, (int)(44 * Scale), IdStatus);
        if (_dark)
            foreach (var id in new[] { IdOpen, IdButton, IdCode })
                SetWindowTheme(GetDlgItem(_pairHwnd, id), id == IdCode ? "DarkMode_CFD" : "DarkMode_Explorer", null);
        SetFocus(open);
    }

    IntPtr Child(string cls, string text, uint style, int x, int y, int w, int h, int id)
    {
        var c = CreateWindowExW(0, cls, text, WS_CHILD | WS_VISIBLE | style, x, y, w, h, _pairHwnd, id, _instance, IntPtr.Zero);
        SendMessageW(c, WM_SETFONT, _font, 1);
        return c;
    }

    /// <summary>A baby from the game (the picture inside the exe); the drawn little face if it cannot be read.</summary>
    static Pixels WelcomePicture(int size)
    {
        try
        {
            using var res = typeof(App).Assembly.GetManifestResourceStream("welcome.png");
            if (res != null)
            {
                Directory.CreateDirectory(Api.Folder);
                var file = Path.Combine(Api.Folder, "welcome.png");
                using (var f = File.Create(file)) res.CopyTo(f);
                if (Sprites.LoadPng(file) is { } png) return Sprites.Scale(png, size, size);
            }
        }
        catch { }
        return Sprites.PetFace(size);
    }

    void Status(string text) { if (_pairHwnd != IntPtr.Zero) SetWindowTextW(GetDlgItem(_pairHwnd, IdStatus), text); }

    unsafe IntPtr PairProc(IntPtr h, uint msg, IntPtr wp, IntPtr lp)
    {
        switch (msg)
        {
            case WM_PAINT:
                {
                    var dc = BeginPaint(h, out var ps);
                    var mem = CreateCompatibleDC(dc);
                    var old = SelectObject(mem, _faceBmp);
                    var inner = PairW - (int)(16 * Scale);
                    AlphaBlend(dc, (inner - _faceSize) / 2, (int)(20 * Scale), _faceSize, _faceSize, mem, 0, 0, _faceSize, _faceSize,
                        new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA });
                    SelectObject(mem, old); DeleteDC(mem);
                    EndPaint(h, ref ps);
                    return IntPtr.Zero;
                }
            case WM_COMMAND when ((int)wp & 0xFFFF) == IdOpen:
                OpenSiteToPair();
                return IntPtr.Zero;
            case WM_COMMAND when ((int)wp & 0xFFFF) == IdButton:
                {
                    var buf = stackalloc char[16];
                    var n = GetWindowTextW(GetDlgItem(h, IdCode), buf, 16);
                    var code = new string(buf, 0, n).Trim();
                    if (code.Length != 6) { Status("Код — это 6 цифр."); return IntPtr.Zero; }
                    Status("Проверяю…");
                    StartPair(code);
                    return IntPtr.Zero;
                }
            case WM_COMMAND when ((int)wp & 0xFFFF) == 1: // Enter: only a typed code counts; the browser opens only by the button
                {
                    var buf = stackalloc char[16];
                    if (GetWindowTextW(GetDlgItem(h, IdCode), buf, 16) > 0) return PairProc(h, WM_COMMAND, IdButton, IntPtr.Zero);
                    return IntPtr.Zero;
                }
            case WM_TIMER when (int)wp == TimerPoll:
                Poll();
                return IntPtr.Zero;
            case WM_CTLCOLORSTATIC:
                {
                    var id = GetDlgCtrlID(lp);
                    SetBkMode(wp, 1);
                    SetTextColor(wp, _dark
                        ? (id is IdSub or IdOr ? 0xA8A2A0u : id == IdStatus ? 0xD8A0F5u : 0xF2EEEBu)
                        : (id is IdSub or IdOr ? 0x75808Fu : id == IdStatus ? 0x8A4DD8u : 0x3A2A26u)); // colours are BGR
                    return _white;
                }
            case WM_CTLCOLOREDIT:
                SetBkMode(wp, 2);
                if (_dark) { SetTextColor(wp, 0xF2EEEB); SetBkColor(wp, 0x2B2B2B); return _editDark; }
                break;
            case WM_ERASEBKGND:
                {
                    var r = new RECT { Right = 10000, Bottom = 10000 };
                    FillRect(wp, ref r, _white);
                    return 1;
                }
            case WM_CLOSE: KillTimer(h, TimerPoll); DestroyWindow(h); _pairHwnd = IntPtr.Zero; _nonce = null; FreePicture(); return IntPtr.Zero;
        }
        return DefWindowProcW(h, msg, wp, lp);
    }

    /// <summary>The site opens with a one-time link; the player presses «Привязать» there, and the app picks up its key.</summary>
    void OpenSiteToPair()
    {
        Status("Открываю сайт…");
        Task.Run(async () =>
        {
            _nonce = await Api.DeskStart();
            PostMessageW(_pet.Hwnd, WM_PAIRED, 1, IntPtr.Zero);
        });
    }

    void Poll()
    {
        if (_nonce is not { } nonce || DateTime.Now > _pollUntil)
        {
            KillTimer(_pairHwnd, TimerPoll);
            if (_nonce != null) Status("Ссылка устарела — нажмите кнопку ещё раз.");
            _nonce = null;
            return;
        }
        Task.Run(async () =>
        {
            var r = await Api.DeskPoll(nonce);
            if (r?.Token is { Length: > 0 } t) { _pairReply = new PairReply { Token = t }; PostMessageW(_pet.Hwnd, WM_PAIRED, IntPtr.Zero, IntPtr.Zero); }
            else if (r?.Expired == true) { _nonce = null; _pairReply = new PairReply { Error = "Ссылка устарела — нажмите кнопку ещё раз." }; PostMessageW(_pet.Hwnd, WM_PAIRED, IntPtr.Zero, IntPtr.Zero); }
        });
    }

    void FreePicture() { if (_faceBmp != IntPtr.Zero) { DeleteObject(_faceBmp); _faceBmp = IntPtr.Zero; } }

    void StartPair(string code) =>
        Task.Run(async () => { _pairReply = await Api.Pair(code); PostMessageW(_pet.Hwnd, WM_PAIRED, IntPtr.Zero, IntPtr.Zero); });

    void OnPaired(bool started)
    {
        if (started)
        {
            if (_nonce == null) { Status("Нет связи с сайтом. Проверьте интернет."); return; }
            ShellExecuteW(IntPtr.Zero, "open", Api.SiteUrl + "/#desk=" + _nonce, null, null, SW_SHOW);
            Status("В браузере нажмите «Привязать» — питомец сразу придёт сюда.");
            _pollUntil = DateTime.Now.AddMinutes(14);
            SetTimer(_pairHwnd, TimerPoll, 2000, IntPtr.Zero);
            return;
        }
        var r = _pairReply;
        if (r?.Token is { Length: > 0 } token)
        {
            _nonce = null;
            // another pet may come now: forget the old one's picture
            _settings.Token = token; _settings.SheetUrl = null; _settings.SheetFile = null; _settings.Name = null; Api.SaveSettings(_settings);
            _sentStrokes = _pet.Strokes; _sentWarm = _pet.WarmTaps;
            if (_pairHwnd != IntPtr.Zero) { KillTimer(_pairHwnd, TimerPoll); DestroyWindow(_pairHwnd); _pairHwnd = IntPtr.Zero; FreePicture(); }
            Sync();
            return;
        }
        Status(r?.Error ?? "Не получилось. Попробуйте ещё раз.");
    }
}
