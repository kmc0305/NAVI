using LocalAssistant.Engine;

namespace LocalAssistant.App;

public class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly HotkeyWindow _hotkeyWindow;
    private readonly ChatWindow _chatWindow;
    private FirestoreBridge? _firestoreBridge;

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

        StartFirestoreBridgeIfConfigured();
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

    private void StartFirestoreBridgeIfConfigured()
    {
        var orchestrator = _chatWindow.Orchestrator;
        var config = FirebaseConfigStore.Load();
        if (orchestrator is null || config is null) return;

        try
        {
            _firestoreBridge = FirestoreBridge.Create(config, orchestrator, _chatWindow);
            _firestoreBridge.StartListening(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // 04-android-windows-bridge.md: 브릿지가 없어도 로컬 채팅은 그대로 동작해야
            // 하므로, 연결 실패는 트레이 알림만 띄우고 앱 전체를 막지 않는다.
            _trayIcon.ShowBalloonTip(5000, "개인 비서", $"Firestore 연결 실패: {ex.Message}", ToolTipIcon.Warning);
        }
    }

    private void ExitApp()
    {
        _trayIcon.Visible = false;
        _hotkeyWindow.Dispose();
        _chatWindow.Dispose();
        if (_firestoreBridge is not null) _ = _firestoreBridge.DisposeAsync();
        ExitThread();
    }
}
