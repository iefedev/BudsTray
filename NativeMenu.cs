using System.Runtime.InteropServices;

namespace BudsTray;

/// A real Win32 popup menu, so it is drawn by Windows itself (light or dark, rounded on
/// Windows 11) like Explorer's tray menus, instead of WinForms' own light ToolStrip.
///
/// Win32 menus have no notion of an item that opens a submenu on hover but also does
/// something when clicked. That is added here by watching mouse clicks inside the menu's
/// modal loop (WH_MSGFILTER) and closing the menu when such an item is clicked.
sealed class NativeMenu : IDisposable
{
    readonly IntPtr _root = CreatePopupMenu();
    readonly List<IntPtr> _submenus = new();
    readonly Dictionary<int, Action> _commands = new();
    readonly List<(IntPtr Menu, int Position, Action Action)> _clickableSubmenus = new();
    int _nextId = 1;
    Action? _clicked;

    public IntPtr Root => _root;

    /// Makes this process's menus light or dark to match the taskbar, as Explorer's tray
    /// menus do. Called before each menu is shown, so a theme change applies to the next one.
    /// Uses the undocumented uxtheme ordinals #135/#136 (Windows 10 1903 and later).
    public static void ApplyTheme(bool light)
    {
        try
        {
            SetPreferredAppMode(light ? ForceLight : ForceDark);
            FlushMenuThemes();
        }
        catch (EntryPointNotFoundException) { } // older Windows: menus stay light
    }

    public void Item(IntPtr menu, string text, Action action, bool check = false, bool radio = false, bool enabled = true)
    {
        int id = _nextId++;
        _commands[id] = action;
        Insert(menu, text, id, IntPtr.Zero, check, radio, enabled);
    }

    public void Label(IntPtr menu, string text) => Insert(menu, text, 0, IntPtr.Zero, false, false, false);

    /// A submenu. With <paramref name="click"/>, clicking the item itself also runs it.
    public IntPtr Submenu(IntPtr menu, string text, Action? click = null, bool check = false, bool radio = false)
    {
        var sub = CreatePopupMenu();
        _submenus.Add(sub);
        int position = GetMenuItemCount(menu);
        Insert(menu, text, 0, sub, check, radio, true);
        if (click is not null) _clickableSubmenus.Add((menu, position, click));
        return sub;
    }

    public void Separator(IntPtr menu)
    {
        var info = new MENUITEMINFO { cbSize = (uint)Marshal.SizeOf<MENUITEMINFO>(), fMask = MIIM_FTYPE, fType = MFT_SEPARATOR };
        InsertMenuItem(menu, (uint)GetMenuItemCount(menu), true, ref info);
    }

    /// Shows the menu at the cursor and runs the chosen action after it closes.
    public void Show(IntPtr owner)
    {
        GetCursorPos(out var pt);
        SetForegroundWindow(owner); // otherwise the menu won't close when clicking elsewhere
        HookProc hook = FilterMessage;
        IntPtr hookHandle = SetWindowsHookEx(WH_MSGFILTER, hook, IntPtr.Zero, GetCurrentThreadId());
        int id;
        try
        {
            id = TrackPopupMenuEx(_root, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, pt.X, pt.Y, owner, IntPtr.Zero);
        }
        finally
        {
            UnhookWindowsHookEx(hookHandle);
            GC.KeepAlive(hook);
        }
        PostMessage(owner, WM_NULL, IntPtr.Zero, IntPtr.Zero);

        var action = id != 0 && _commands.TryGetValue(id, out var a) ? a : _clicked;
        action?.Invoke();
    }

    IntPtr FilterMessage(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code == MSGF_MENU)
        {
            var msg = Marshal.PtrToStructure<MSG>(lParam);
            if (msg.message == WM_LBUTTONUP)
            {
                IntPtr shown = SendMessage(msg.hwnd, MN_GETHMENU, IntPtr.Zero, IntPtr.Zero);
                int position = MenuItemFromPoint(IntPtr.Zero, shown, msg.pt);
                foreach (var (menu, pos, action) in _clickableSubmenus)
                    if (menu == shown && pos == position)
                    {
                        _clicked = action;
                        EndMenu();
                        return (IntPtr)1;
                    }
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    static void Insert(IntPtr menu, string text, int id, IntPtr submenu, bool check, bool radio, bool enabled)
    {
        var info = new MENUITEMINFO
        {
            cbSize = (uint)Marshal.SizeOf<MENUITEMINFO>(),
            fMask = MIIM_STRING | MIIM_ID | MIIM_STATE | MIIM_FTYPE | (submenu != IntPtr.Zero ? MIIM_SUBMENU : 0),
            fType = MFT_STRING | (radio ? MFT_RADIOCHECK : 0),
            fState = (check || radio ? MFS_CHECKED : 0) | (enabled ? 0 : MFS_DISABLED),
            wID = (uint)id,
            hSubMenu = submenu,
            dwTypeData = text.Replace("&", "&&"),
        };
        InsertMenuItem(menu, (uint)GetMenuItemCount(menu), true, ref info);
    }

    public void Dispose() => DestroyMenu(_root); // destroys the submenus with it

    // --- Win32 ---
    const int ForceDark = 2, ForceLight = 3;
    const int WH_MSGFILTER = -1, MSGF_MENU = 2;
    const uint WM_NULL = 0, WM_LBUTTONUP = 0x0202, MN_GETHMENU = 0x01E1;
    const uint TPM_RETURNCMD = 0x0100, TPM_RIGHTBUTTON = 0x0002, TPM_BOTTOMALIGN = 0x0020;
    const uint MIIM_STATE = 0x1, MIIM_ID = 0x2, MIIM_SUBMENU = 0x4, MIIM_STRING = 0x40, MIIM_FTYPE = 0x100;
    const uint MFT_STRING = 0, MFT_SEPARATOR = 0x800, MFT_RADIOCHECK = 0x200;
    const uint MFS_CHECKED = 0x8, MFS_DISABLED = 0x3;

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MENUITEMINFO
    {
        public uint cbSize, fMask, fType, fState, wID;
        public IntPtr hSubMenu, hbmpChecked, hbmpUnchecked, dwItemData;
        public string dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    [DllImport("uxtheme.dll", EntryPoint = "#135")] static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136")] static extern void FlushMenuThemes();
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool InsertMenuItem(IntPtr menu, uint item, bool byPosition, ref MENUITEMINFO info);
    [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] static extern int MenuItemFromPoint(IntPtr hwnd, IntPtr menu, POINT pt);
    [DllImport("user32.dll")] static extern bool EndMenu();
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
}
