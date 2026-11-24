using System.Drawing;
using System.Windows.Forms;

namespace LocalAssistant.Engine;

public static class ClipboardImage
{
    // System.Windows.Forms.Clipboard requires an STA thread, which a console app's
    // Main is not by default, so this runs the set on a dedicated STA thread.
    public static void SetImage(Bitmap bitmap)
    {
        var thread = new Thread(() => Clipboard.SetImage(bitmap));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }
}
