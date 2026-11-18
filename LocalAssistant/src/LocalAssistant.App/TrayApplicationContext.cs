namespace LocalAssistant.App;

public class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly HotkeyWindow _hotkeyWindow;
    private readonly ChatWindow _chatWindow;

    public TrayApplicationContext()
    {
        _chatWindow = new ChatWindow();

        var menu = new ContextMenuStrip();
        menu.Items.Add("채팅 열기/숨기기", null, (_, _) => ToggleChat());
        menu.Items.Add("종료", null, (_, _) => ExitApp());

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "개인 비서",
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => ToggleChat();

        _hotkeyWindow = new HotkeyWindow();
        _hotkeyWindow.HotkeyPressed += ToggleChat;
        _hotkeyWindow.Register(); // Ctrl+Alt+Space
    }

    private void ToggleChat()
    {
        if (_chatWindow.Visible)
        {
            _chatWindow.Hide();
        }
        else
        {
            _chatWindow.Show();
            _chatWindow.Activate();
        }
    }

    private void ExitApp()
    {
        _trayIcon.Visible = false;
        _hotkeyWindow.Dispose();
        _chatWindow.Dispose();
        ExitThread();
    }
}
