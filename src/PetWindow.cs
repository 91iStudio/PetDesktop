using static PetDesktop.Native;

namespace PetDesktop;

/// <summary>
/// The pet on the taskbar: one transparent window, redrawn only when the frame changes.
/// Standing — a blink every few seconds; walking — 7 frames a second; asleep — no timers at all (a mouse over it wakes it, unless it sleeps on the site: then it purrs);
/// a full-screen game or film — hidden, with only a check every 7 seconds.
/// </summary>
sealed class PetWindow
{
    enum State { None, Idle, Walk, Pose, Sleep, Held, Fall, Jump, Hidden }

    const int TimerAnim = 1, TimerSystem = 2, TimerSync = 3, TimerCursor = 4, TimerHideHour = 5, TimerHeart = 6;

    public IntPtr Hwnd { get; }
    readonly IntPtr _heartHwnd;
    readonly App _app;
    readonly Random _rng = new();
    Sprites? _sprites;
    IntPtr _memDc;
    readonly IntPtr _heartBmp;
    readonly int _heartSize;

    State _state = State.None;
    int _frame = -1, _dir = 1;
    double _x, _y, _lift, _vy;
    RECT _work;
    double _walkTarget;
    Action? _then;
    int _blinksLeft;
    bool _blinkPhase;
    long _jumpStart;
    bool _hiddenByGame, _hiddenByUser, _batterySaver;
    public bool Sad, ServerAsleep;
    /// <summary>The baby is still inside: the egg sits on the taskbar, wobbles now and then, and strokes warm it.</summary>
    public bool IsEgg;
    public int WarmTaps;
    public string Character = "";
    DateTime _lastTouch = DateTime.Now;

    bool _tracking, _pressed;
    POINT _pressAt, _lastMouse;
    long _pressTick;
    double _rub;
    public int Strokes, Redraws;
    public double VisibleSeconds;
    int _heartStep = -1;
    // the pendulum while held: angle in degrees, its speed, and how far the mouse moved since the last tick
    double _swing, _swingVel; int _lastHeldX, _drawnSwing = -1;
    POINT _heartAt;

    public PetWindow(App app, IntPtr instance)
    {
        _app = app;
        Hwnd = CreateWindowExW(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE, App.PetClass, "Пушиш", WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        _heartHwnd = CreateWindowExW(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT, App.PetClass, null, WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        var screen = GetDC(IntPtr.Zero);
        _memDc = CreateCompatibleDC(screen);
        ReleaseDC(IntPtr.Zero, screen);
        _heartSize = (int)(28 * App.Scale);
        _heartBmp = Sprites.Dib(Sprites.Heart(_heartSize));
        ReadWorkArea();
        SetTimer(Hwnd, TimerSystem, 7000, IntPtr.Zero);
    }

    public bool HasPet => _sprites != null;

    /// <summary>New frames (a new pet, a new size): keep the place, start standing.</summary>
    public void SetSprites(Sprites s)
    {
        var first = _sprites == null;
        _sprites?.Dispose();
        _sprites = s;
        _frame = -1;
        if (first) { _x = _work.Right - s.BoxW * 3; _y = Floor; _dir = -1; }
        _y = Math.Min(_y, Floor);
        if (_hiddenByGame || _hiddenByUser) return;
        ShowWindow(Hwnd, SW_SHOWNOACTIVATE);
        if (ServerAsleep) Sleep(); else Pose(Sprites.Wave, 1300);
    }

    double Floor => _work.Bottom + 1;
    int BoxW => _sprites?.BoxW ?? 1;
    int BoxH => _sprites?.BoxH ?? 1;

    void ReadWorkArea()
    {
        SystemParametersInfoW(SPI_GETWORKAREA, 0, out _work, 0);
        if (_sprites != null && _state != State.Held) { _x = Math.Clamp(_x, _work.Left + BoxW / 2.0, _work.Right - BoxW / 2.0); if (_state != State.Jump) _y = Floor; Draw(); }
    }

    // ───── Drawing ─────

    void Show(int frame) { if (frame == _frame) return; _frame = frame; Draw(); }

    void Draw()
    {
        if (_sprites == null || _frame < 0 || _hiddenByGame || _hiddenByUser) return;
        var old = SelectObject(_memDc, _state == State.Held && _frame == Sprites.Held ? _sprites.GetHeld(SwingIndex, _dir) : _sprites.Get(_frame, _dir));
        var pos = new POINT { X = (int)Math.Round(_x - BoxW / 2.0), Y = (int)Math.Round(_y - BoxH - _lift) };
        var size = new SIZE { CX = BoxW, CY = BoxH };
        var zero = new POINT();
        var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
        UpdateLayeredWindow(Hwnd, IntPtr.Zero, ref pos, ref size, _memDc, ref zero, 0, ref blend, ULW_ALPHA);
        SelectObject(_memDc, old);
        Redraws++;
    }

    int SwingIndex
    {
        get
        {
            var a = Sprites.SwingAngles;
            var best = 0;
            for (var i = 1; i < a.Length; i++) if (Math.Abs(a[i] - _swing) < Math.Abs(a[best] - _swing)) best = i;
            return best;
        }
    }

    void Anim(uint ms) => SetTimer(Hwnd, TimerAnim, ms, IntPtr.Zero);
    void StopAnim() => KillTimer(Hwnd, TimerAnim);
    int Rest => Sad ? Sprites.Sad : Sprites.Stand;
    uint Rnd(int a, int b) => (uint)_rng.Next(a, b);
    static long Now => Environment.TickCount64;

    // ───── Behaviour ─────

    void Idle()
    {
        _state = State.Idle; _then = null;
        Show(Rest);
        _blinksLeft = _rng.Next(2, 6); _blinkPhase = false;
        Anim(Rnd(1800, 3800));
        SetTimer(Hwnd, TimerCursor, 500, IntPtr.Zero);
    }

    void Decide()
    {
        if (IsEgg) { Wobble(); return; }
        var hour = DateTime.Now.Hour;
        var lonely = (DateTime.Now - _lastTouch).TotalMinutes;
        var sleepy = Character == "sleepy" ? 0.25 : 0.08;
        if (!IsEgg && (ServerAsleep || _batterySaver) || hour >= 23 || hour < 7 || (lonely > 20 && _rng.NextDouble() < sleepy)) { GoSleep(); return; }
        var r = _rng.NextDouble();
        if (Sad && r < 0.3) { Pose(Sprites.Sad, 2500); return; }
        if (r < 0.5) { Wander(); return; }
        if (r < 0.66 || Character == "sleepy" && r < 0.75) { Pose(Sprites.Sit, (int)Rnd(3000, 7000)); return; }
        if (r < 0.76) { Pose(Sprites.Wave, 1300); return; }
        if (r < 0.9 || Character == "playful") { Pose(Sprites.Play, 2200); return; }
        Pose(Sprites.Happy, 900);
    }

    /// <summary>The egg rocks a little: left, right, left, still.</summary>
    void Wobble() => Pose(Sprites.Walk1, 130, () => Pose(Sprites.Walk2, 130, () => Pose(Sprites.Walk1, 130, () => Idle())));

    void Pose(int frame, int ms, Action? then = null)
    {
        _state = State.Pose; _then = then;
        Show(frame);
        Anim((uint)ms);
    }

    void Wander()
    {
        var shy = Character == "shy" ? 0.5 : 1;
        var t = _x + _rng.Next(80, 360) * App.Scale * shy * (_rng.Next(2) == 0 ? -1 : 1);
        double a = _work.Left + BoxW / 2.0, b = _work.Right - BoxW / 2.0;
        if (t < a || t > b) t = _x - (t - _x);
        WalkTo(Math.Clamp(t, a, b));
    }

    void WalkTo(double target, Action? then = null)
    {
        _state = State.Walk; _then = then; _walkTarget = target;
        KillTimer(Hwnd, TimerCursor);
        _dir = target >= _x ? 1 : -1;
        Anim(1000 / 7);
    }

    void GoSleep() => WalkTo(_work.Right - BoxW * 0.8, Sleep);

    void Sleep()
    {
        _state = State.Sleep; _dir = 1; _lift = 0;
        StopAnim(); KillTimer(Hwnd, TimerCursor);
        _frame = -1; Show(Sprites.Sleep);
    }

    void Wake()
    {
        _lastTouch = DateTime.Now;
        Pose(Sprites.Blink, 400, () => Pose(Sprites.Wave, 1200));
    }

    void Jump()
    {
        _state = State.Jump; _jumpStart = Now;
        KillTimer(Hwnd, TimerCursor);
        Show(Sprites.Happy);
        Anim(16);
    }

    void OnAnim()
    {
        switch (_state)
        {
            case State.Idle:
                if (_blinkPhase) { _blinkPhase = false; Show(Rest); if (--_blinksLeft <= 0) Decide(); else Anim(Rnd(1800, 3800)); }
                else { _blinkPhase = true; Show(Sprites.Blink); Anim(150); }
                break;
            case State.Walk:
                var step = Math.Max(3, BoxH / 10.0);
                if (Math.Abs(_walkTarget - _x) <= step)
                {
                    _x = _walkTarget; _lift = 0;
                    var then = _then; if (then != null) then(); else Idle();
                    if (_state == State.Walk) break;
                    Draw(); break;
                }
                _x += _dir * step;
                _frame = _frame == Sprites.Walk1 ? Sprites.Walk2 : Sprites.Walk1;
                _lift = _frame == Sprites.Walk1 ? 2 * App.Scale : 0;
                Draw();
                break;
            case State.Pose:
                { var then = _then; if (then != null) { _then = null; then(); } else Idle(); }
                break;
            case State.Jump:
                var k = Math.Min(1, (Now - _jumpStart) / 450.0);
                _lift = Math.Sin(Math.PI * k) * BoxH * 0.35;
                Draw();
                if (k >= 1) { _lift = 0; Draw(); Pose(Sprites.Happy, 500); }
                break;
            case State.Held:
                {
                    // a pendulum: pulled back to the middle, slowly calming down, pushed by the hand moving sideways
                    GetCursorPos(out var hp);
                    var push = (hp.X - _lastHeldX) / App.Scale; _lastHeldX = hp.X;
                    _swingVel += -0.35 * _swing - 0.08 * _swingVel - push * 0.5;
                    _swing = Math.Clamp(_swing + _swingVel, -12, 12);
                    var idx = SwingIndex;
                    if (idx != _drawnSwing) { _drawnSwing = idx; Draw(); }
                }
                break;
            case State.Fall:
                _vy += 1.6 * App.Scale; _y += _vy;
                if (_y >= Floor)
                {
                    _y = Floor; Draw();
                    // asleep on the site: a sleepy blink and it curls up right where it landed (no walk back to the clock)
                    if (ServerAsleep) Pose(Sprites.Blink, 500, Sleep); else Pose(Sprites.Happy, 700);
                }
                else Draw();
                break;
            default: StopAnim(); break;
        }
    }

    /// <summary>Looks at the mouse when it comes near (twice a second, only while standing or sitting).</summary>
    void OnCursor()
    {
        if (_state is not (State.Idle or State.Pose)) { KillTimer(Hwnd, TimerCursor); return; }
        GetCursorPos(out var p);
        double dx = p.X - _x, dy = p.Y - (_y - BoxH / 2.0);
        if (Math.Abs(dx) > BoxW * 4 || Math.Abs(dy) > BoxH * 3 || Math.Abs(dx) < BoxW * 0.3) return;
        var d = dx > 0 ? 1 : -1;
        if (d != _dir) { _dir = d; Draw(); }
        if (Character == "playful" && _state == State.Idle && Math.Abs(dx) > BoxW && _rng.NextDouble() < 0.1)
            WalkTo(Math.Clamp(p.X - d * BoxW * 0.6, _work.Left + BoxW / 2.0, _work.Right - BoxW / 2.0));
    }

    // ───── System ─────

    /// <summary>Every 7 seconds: hide from full-screen games, films and presentations; sleep in battery saver; follow the taskbar.</summary>
    void OnSystem()
    {
        var busy = SHQueryUserNotificationState(out var q) == 0 && q is 2 or 3 or 4;
        if (busy != _hiddenByGame)
        {
            _hiddenByGame = busy;
            if (busy) { StopAnim(); KillTimer(Hwnd, TimerCursor); ShowWindow(Hwnd, SW_HIDE); _state = State.Hidden; }
            else if (!_hiddenByUser && _sprites != null) { ShowWindow(Hwnd, SW_SHOWNOACTIVATE); _frame = -1; Idle(); }
        }
        _batterySaver = GetSystemPowerStatus(out var ps) && ps.SystemStatusFlag == 1;
        if (!_hiddenByGame && !_hiddenByUser && _sprites != null) VisibleSeconds += 7;
    }

    public bool HiddenByUser => _hiddenByUser;

    public void HideForHour(bool hide)
    {
        _hiddenByUser = hide;
        if (hide) { StopAnim(); KillTimer(Hwnd, TimerCursor); ShowWindow(Hwnd, SW_HIDE); _state = State.Hidden; SetTimer(Hwnd, TimerHideHour, 3_600_000, IntPtr.Zero); }
        else { KillTimer(Hwnd, TimerHideHour); if (!_hiddenByGame && _sprites != null) { ShowWindow(Hwnd, SW_SHOWNOACTIVATE); _frame = -1; Idle(); } }
    }

    /// <summary>The server says the pet sleeps (or woke up).</summary>
    public void SetAsleep(bool asleep)
    {
        var was = ServerAsleep; ServerAsleep = asleep;
        if (IsEgg) return;
        if (_sprites == null || _state is State.Hidden or State.Held or State.Fall) return;
        if (asleep && !was && _state != State.Sleep) GoSleep();
        if (!asleep && was && _state == State.Sleep) Wake();
    }

    public void Refresh() { if (_state is State.Idle or State.Pose or State.Sleep) { var f = _frame; _frame = -1; Show(_state == State.Idle ? Rest : f); } }

    // ───── Hands ─────

    void OnMouseMove()
    {
        GetCursorPos(out var p);
        var d = Math.Sqrt(Math.Pow(p.X - _lastMouse.X, 2) + Math.Pow(p.Y - _lastMouse.Y, 2));
        _lastMouse = p;
        if (!_tracking)
        {
            var t = new TRACKMOUSEEVENT { Size = (uint)sizeof_TME, Flags = TME_LEAVE, Hwnd = Hwnd };
            TrackMouseEvent(ref t); _tracking = true; d = 0;
        }
        if (_pressed)
        {
            if (_state != State.Held && Math.Abs(p.X - _pressAt.X) + Math.Abs(p.Y - _pressAt.Y) > 6 * App.Scale)
            {
                _state = State.Held; KillTimer(Hwnd, TimerCursor); _lift = 0;
                _swing = 0; _swingVel = 6; _lastHeldX = p.X;
                _frame = -1; Show(Sprites.Held);
                Anim(85); // the swing: ~12 frames a second, only while held
            }
            if (_state == State.Held) { _x = p.X; _y = Math.Min(Floor, p.Y + BoxH * 0.88); Draw(); }
            return;
        }
        if (_state is State.Held or State.Fall or State.Jump or State.Hidden) return;
        _rub += d;
        // asleep on the site: it purrs in its sleep (strokes count) and wakes only when woken there — the same everywhere
        if (_state == State.Sleep && ServerAsleep) { if (_rub > 110 * App.Scale) { _rub = 0; Strokes++; Heart(); } return; }
        if (_state == State.Sleep) { if (_rub > 40 * App.Scale) { _rub = 0; Wake(); } return; }
        if (_rub > 110 * App.Scale)
        {
            _rub = 0; _lastTouch = DateTime.Now;
            Heart();
            if (IsEgg) { WarmTaps++; if (_state != State.Pose) Wobble(); return; }
            Strokes++;
            if (_state != State.Walk) Pose(Sprites.Happy, 800);
        }
    }

    static readonly int sizeof_TME = System.Runtime.InteropServices.Marshal.SizeOf<TRACKMOUSEEVENT>();

    void OnDown()
    {
        GetCursorPos(out _pressAt); _pressTick = Now; _pressed = true;
        SetCapture(Hwnd);
    }

    void OnUp()
    {
        var was = _pressed; _pressed = false;
        ReleaseCapture();
        if (!was) return;
        _lastTouch = DateTime.Now;
        if (_state == State.Held) { _state = State.Fall; _vy = 0; _frame = -1; Show(Sprites.Held); Anim(16); return; }
        if (Now - _pressTick < 400)
        {
            if (_state == State.Sleep) { if (ServerAsleep) Heart(); else Wake(); return; }
            if (IsEgg) { if (_state is State.Idle or State.Pose) Wobble(); return; }
            if (_state is State.Idle or State.Pose or State.Walk) Jump();
        }
    }

    /// <summary>A heart floats up from the pet (its own little window, only for that second).</summary>
    void Heart()
    {
        _heartAt = new POINT { X = (int)(_x - _heartSize / 2.0 + _rng.Next(-BoxW / 4, BoxW / 4 + 1)), Y = (int)(_y - BoxH * 0.85) };
        _heartStep = 0;
        ShowWindow(_heartHwnd, SW_SHOWNOACTIVATE);
        SetTimer(Hwnd, TimerHeart, 50, IntPtr.Zero);
        OnHeart();
    }

    void OnHeart()
    {
        if (_heartStep < 0) return;
        const int steps = 18;
        var k = _heartStep / (double)steps;
        var old = SelectObject(_memDc, _heartBmp);
        var pos = new POINT { X = _heartAt.X, Y = _heartAt.Y - (int)(k * 45 * App.Scale) };
        var size = new SIZE { CX = _heartSize, CY = _heartSize };
        var zero = new POINT();
        var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = (byte)(255 * (1 - k * k)), AlphaFormat = AC_SRC_ALPHA };
        UpdateLayeredWindow(_heartHwnd, IntPtr.Zero, ref pos, ref size, _memDc, ref zero, 0, ref blend, ULW_ALPHA);
        SelectObject(_memDc, old);
        if (++_heartStep > steps) { _heartStep = -1; KillTimer(Hwnd, TimerHeart); ShowWindow(_heartHwnd, SW_HIDE); }
    }

    public IntPtr Handle(IntPtr h, uint msg, IntPtr wp, IntPtr lp)
    {
        if (h != Hwnd) return DefWindowProcW(h, msg, wp, lp);
        switch (msg)
        {
            case WM_TIMER:
                switch ((int)wp)
                {
                    case TimerAnim: OnAnim(); break;
                    case TimerSystem: OnSystem(); break;
                    case TimerSync: _app.Sync(); break;
                    case TimerCursor: OnCursor(); break;
                    case TimerHideHour: HideForHour(false); break;
                    case TimerHeart: OnHeart(); break;
                }
                return IntPtr.Zero;
            case WM_MOUSEMOVE: OnMouseMove(); return IntPtr.Zero;
            case WM_MOUSELEAVE: _tracking = false; _rub = 0; return IntPtr.Zero;
            case WM_LBUTTONDOWN: OnDown(); return IntPtr.Zero;
            case WM_LBUTTONUP: OnUp(); return IntPtr.Zero;
            case WM_LBUTTONDBLCLK: if (IsEgg) _app.EggDoubleClick(); else if (_state is State.Idle or State.Pose) Jump(); return IntPtr.Zero;
            case WM_RBUTTONUP: _app.ShowMenu(); return IntPtr.Zero;
            case WM_DISPLAYCHANGE:
            case WM_SETTINGCHANGE: ReadWorkArea(); break;
        }
        return _app.HandleShared(h, msg, wp, lp);
    }

    public void StartSync(uint everyMs) => SetTimer(Hwnd, TimerSync, everyMs, IntPtr.Zero);
}
