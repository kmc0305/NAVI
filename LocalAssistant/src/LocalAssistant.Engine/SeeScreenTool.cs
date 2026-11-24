using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using FlaUI.UIA3;

namespace LocalAssistant.Engine;

// gui_input과 짝 - dump_window(접근성 트리, 텍스트)와 달리 사람이 보는 것과 똑같은
// 화면 이미지를 Claude에게 그대로 보여준다. Claude가 이미지에서 클릭할 위치를 찾으면
// 그 절대 화면 좌표를 gui_input의 click_at(x,y)에 그대로 넘기면 된다 - 이 도구가
// 보고하는 좌표계(SystemInformation.VirtualScreen 기준)와 click_at의 Mouse.MoveTo가
// 쓰는 좌표계가 동일하다.
public class SeeScreenTool : ITool, IImageTool
{
    public string Name => "see_screen";
    public string Description =>
        "PC 화면을 스크린샷으로 캡쳐해서 이미지로 직접 보여준다. titleContains를 주면 그 창만 " +
        "크게 캡쳐한다(특정 앱 안에서 정확히 클릭해야 할 때는 이 방식을 써야 버튼이 작아서 " +
        "못 찾는 문제가 없다). titleContains 없이 호출하면 화면 전체를 캡쳐한다(전체 구도를 봐야 할 때). " +
        "화면을 눈으로 보고 어디를 클릭해야 할지 스스로 판단해야 할 때 사용하고, " +
        "판단한 좌표는 gui_input의 click_at(x,y)에 그대로 넘기면 된다 (같은 좌표계).";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["titleContains"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "지정하면 전체 화면 대신 제목에 이 문자열이 포함된 창만 캡쳐 (더 정확한 클릭을 위해 권장)"
            }
        }
    };

    public async Task<string> ExecuteAsync(JsonElement input, Action<string> log)
    {
        var result = await ExecuteImageAsync(input, log);
        return result.Text;
    }

    // Claude의 비전 처리가 아주 큰 이미지(예: 멀티모니터 3840x1200 전체 화면)를 내부적으로
    // 다시 축소해버려서, 원본 해상도를 그대로 보내면 Claude가 이미지에서 "보는" 좌표와 실제
    // 화면 좌표가 어긋나는 걸 실측으로 확인함 - 그래서 Claude에게 보내기 전에 여기서 먼저
    // 적당한 크기로 줄이고, 그 축소율을 ScreenCoordinateMap에 기록해서 gui_input.click_at이
    // Claude가 읽은 이미지 좌표를 실제 화면 좌표로 환산하게 한다. 또한 전체 화면 캡쳐는
    // 작은 버튼이 더 작게 눌려서 정확한 클릭이 어려우므로, titleContains가 있으면 그 창만
    // PrintWindow로 캡쳐해(ScreenshotWindowTool과 동일한 방식) 디테일을 보존한다.
    private const int MaxLongEdge = 1280;

    public Task<ToolImageResult> ExecuteImageAsync(JsonElement input, Action<string> log)
    {
        var titleContains = input.TryGetProperty("titleContains", out var t) ? t.GetString() : null;

        if (!string.IsNullOrWhiteSpace(titleContains))
        {
            log($"창 캡쳐 중: {titleContains}");
            var captured = CaptureWindow(titleContains);
            if (captured is null)
                return Task.FromResult(new ToolImageResult(
                    $"창을 찾지 못했습니다: 제목에 \"{titleContains}\" 포함된 창 없음.", null));

            var (bitmap, screenRect) = captured.Value;
            using (bitmap)
                return Task.FromResult(ResizeAndEncode(bitmap, screenRect));
        }

        log("화면 전체 캡쳐 중");
        var bounds = SystemInformation.VirtualScreen;
        using var fullBitmap = new Bitmap(bounds.Width, bounds.Height);
        using (var g = Graphics.FromImage(fullBitmap))
        {
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
        }
        return Task.FromResult(ResizeAndEncode(fullBitmap, bounds));
    }

    private static (Bitmap Bitmap, Rectangle ScreenRect)? CaptureWindow(string titleContains)
    {
        using var automation = new UIA3Automation();
        var window = WindowFinder.FindWindow(automation, titleContains);
        if (window is null || !window.Properties.NativeWindowHandle.IsSupported) return null;

        var rect = window.BoundingRectangle;
        var bitmap = WindowPrintCapture.CaptureWindow(window.Properties.NativeWindowHandle.Value, rect.Width, rect.Height);
        return bitmap is null ? null : (bitmap, rect);
    }

    private static string? SafeGet(Func<string?> getter)
    {
        try { return getter(); } catch { return null; }
    }

    private static ToolImageResult ResizeAndEncode(Bitmap source, Rectangle screenRect)
    {
        var longEdge = Math.Max(screenRect.Width, screenRect.Height);
        var scale = longEdge > MaxLongEdge ? (double)MaxLongEdge / longEdge : 1.0;
        var scaledWidth = Math.Max(1, (int)Math.Round(screenRect.Width * scale));
        var scaledHeight = Math.Max(1, (int)Math.Round(screenRect.Height * scale));

        using var scaledBitmap = new Bitmap(scaledWidth, scaledHeight);
        using (var g = Graphics.FromImage(scaledBitmap))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, 0, 0, scaledWidth, scaledHeight);
        }

        var captureId = ScreenCoordinateMap.Set(
            screenRect.Width / (double)scaledWidth,
            screenRect.Height / (double)scaledHeight,
            screenRect.X,
            screenRect.Y);

        // JPEG로 인코딩 - Claude의 비전 토큰 비용은 픽셀 해상도로만 정해져서 PNG든 JPEG든
        // 동일하지만, 전송 바이트 수는 JPEG가 훨씬 작아서(특히 로컬 게이트웨이를 한 번 더
        // 거치는 구성에서) 왕복 시간이 줄어든다. UI 스크린샷은 큰 버튼/텍스트 위주라 JPEG
        // 압축 아티팩트가 클릭 판단에 영향을 줄 정도는 아니다.
        using var stream = new MemoryStream();
        scaledBitmap.Save(stream, ImageFormat.Jpeg);

        var text = $"화면 캡쳐됨 (이미지 크기 {scaledWidth}x{scaledHeight}, capture_id={captureId}). " +
                   "이 이미지에서 본 픽셀 좌표를 click_at(x,y)에 넘길 때 captureId도 반드시 이 " +
                   $"{captureId}를 그대로 같이 넘겨라 - 실제 화면 좌표로는 자동으로 환산되니 배율 계산은 " +
                   "하지 말 것. 이 사이에 see_screen을 또 호출했다면 captureId가 바뀌니, 그때는 가장 " +
                   "마지막으로 받은 capture_id를 써야 한다(안 그러면 클릭이 거부된다).";

        return new ToolImageResult(text, stream.ToArray(), "image/jpeg");
    }
}
