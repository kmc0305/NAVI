using System.Collections.Specialized;
using System.Windows.Forms;

namespace LocalAssistant.Engine;

// ClipboardImage(이미지)와 같은 패턴 - 파일 경로를 CF_HDROP 형식으로 클립보드에 올려서
// 카카오톡 같은 앱의 메시지 입력창에 Ctrl+V로 "파일 첨부"를 흉내낸다. 파일첨부 아이콘
// 자체가 커스텀 드로잉이라 UIA로 못 찾는 앱에서, 아이콘을 안 눌러도 되는 경로.
public static class ClipboardFiles
{
    public static void SetFiles(IEnumerable<string> paths)
    {
        var collection = new StringCollection();
        collection.AddRange(paths.ToArray());

        var thread = new Thread(() => Clipboard.SetFileDropList(collection));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }
}
