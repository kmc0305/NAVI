using System.Runtime.InteropServices;

namespace LocalAssistant.App;

// Message-only native window used purely to receive WM_HOTKEY - RegisterHotKey
// needs some window handle to post the message to.
public class HotkeyWindow : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 1;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public event Action? HotkeyPressed;

    public HotkeyWindow()
    {
        CreateHandle(new CreateParams());
    }

    // MOD_CONTROL(2) + MOD_ALT(1) = 3, VK_SPACE = 0x20
    public void Register(uint modifiers = 3, uint virtualKey = 0x20)
    {
        RegisterHotKey(Handle, HotkeyId, modifiers, virtualKey);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
            HotkeyPressed?.Invoke();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        UnregisterHotKey(Handle, HotkeyId);
        DestroyHandle();
    }
}
