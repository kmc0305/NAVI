namespace LocalAssistant.Engine;

// SeeScreenTool과 GuiInputTool 사이에서만 쓰는 공유 상태. Claude의 비전 처리가 큰
// 스크린샷(예: 멀티모니터 3840x1200)을 내부적으로 다시 축소해버려서, Claude가 이미지에서
// "보는" 픽셀 좌표와 실제 화면 좌표가 어긋나는 문제가 실측으로 확인됨(see_screen -> 8번
// 버튼 클릭 시도 -> 실제로는 다른 곳을 클릭). 그래서 SeeScreenTool이 이미지를 보내기 전에
// 미리 작게 리사이즈하고 그 축소율을 여기에 기록해두면, click_at이 Claude가 이미지에서
// 읽은 좌표를 그대로 받아 실제 화면 좌표로 환산한다 - Claude가 스케일 계산을 직접 할
// 필요가 없어진다.
// 버그(실측): see_screen을 연달아 두 번 부르면(예: 창 캡쳐 후 확인용 전체화면 캡쳐) 두
// 번째 캡쳐가 이 전역 상태를 덮어써서, Claude가 첫 번째(창) 이미지를 보고 판단한 좌표를
// click_at에 넘겨도 두 번째(전체화면) 캡쳐의 배율/오프셋으로 환산되어 완전히 다른 곳을
// 클릭하게 됨 - 조용히 틀린 위치를 클릭하는 게 문제였다. Generation을 캡쳐마다 올리고
// click_at이 자신이 본 캡쳐의 번호(captureId)를 같이 넘기게 해서, 그 사이에 다른 캡쳐가
// 끼어들었으면(번호 불일치) 틀린 위치를 클릭하는 대신 명확히 거부한다.
public static class ScreenCoordinateMap
{
    public static double ScaleX { get; private set; } = 1.0;
    public static double ScaleY { get; private set; } = 1.0;
    public static int OffsetX { get; private set; }
    public static int OffsetY { get; private set; }
    public static long Generation { get; private set; }

    public static long Set(double scaleX, double scaleY, int offsetX, int offsetY)
    {
        ScaleX = scaleX;
        ScaleY = scaleY;
        OffsetX = offsetX;
        OffsetY = offsetY;
        return ++Generation;
    }

    public static (int X, int Y) ImageToScreen(int imageX, int imageY) =>
        (OffsetX + (int)Math.Round(imageX * ScaleX), OffsetY + (int)Math.Round(imageY * ScaleY));
}
