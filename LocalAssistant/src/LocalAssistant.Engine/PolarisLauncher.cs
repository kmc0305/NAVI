using System.Diagnostics;
using FlaUI.UIA3;

namespace LocalAssistant.Engine;

public static class PolarisLauncher
{
    private const string HubExePath = @"C:\Program Files (x86)\Polaris Office\Office\10.105.306.56235\Binary\PolarisOffice.exe";
    private const string HubWindowTitle = "Polaris Office";
    private const string PSlideProcessName = "PSlide";

    // Opens a brand-new blank presentation via the Polaris Office hub's "PPTX"
    // quick-create shortcut and returns the new window's exact title (unique
    // enough to use as a windowTitleSubstring for the rest of the pipeline), or
    // null on failure. The new document is identified by diffing PSlide.exe
    // process IDs before/after the click - never by guessing a title - so any
    // document the user already has open elsewhere is never touched.
    public static string? OpenBlankPresentation(Action<string> log)
    {
        var beforePids = RunningPSlidePids();

        if (!EnsureHubRunning(log))
            return null;

        if (!WaitForHubWindow(TimeSpan.FromSeconds(15)))
        {
            log("Polaris Office 허브 창을 찾지 못했습니다.");
            return null;
        }

        if (!ButtonClicker.ClickButtonByName(HubWindowTitle, "PPTX"))
        {
            log("Polaris Office 허브의 \"PPTX\" 바로가기를 클릭하지 못했습니다.");
            return null;
        }

        var newPid = WaitForNewPSlideProcess(beforePids, TimeSpan.FromSeconds(20));
        if (newPid is null)
        {
            log("새 PSlide 프로세스를 찾지 못했습니다.");
            return null;
        }

        // Wait for readiness by process/window handle, not by title: the title
        // starts out as a generic placeholder (observed: "Polaris Slide") before
        // PSlide renames it to the real document name a moment later, so reading
        // it too early and matching by that substring afterwards would silently
        // stop finding the window. The handle never changes, so there is nothing
        // to race here - read the (by-then-stable) title only once ready.
        if (!WaitForHomeTabReadyByPid(newPid.Value, TimeSpan.FromSeconds(25)))
        {
            log("새 프레젠테이션 창이 준비 상태가 되지 않았습니다.");
            return null;
        }

        var title = ReadWindowTitle(newPid.Value);
        if (title is null)
        {
            log("새 프레젠테이션 창 제목을 읽지 못했습니다.");
            return null;
        }

        log($"새 프레젠테이션 창 준비 완료: \"{title}\"");
        return title;
    }

    private static HashSet<int> RunningPSlidePids() =>
        Process.GetProcessesByName(PSlideProcessName).Select(p => p.Id).ToHashSet();

    private static bool EnsureHubRunning(Action<string> log)
    {
        if (Process.GetProcessesByName("PolarisOffice").Length > 0)
            return true;

        if (!File.Exists(HubExePath))
        {
            log($"Polaris Office 실행 파일을 찾지 못했습니다: {HubExePath}");
            return false;
        }

        Process.Start(HubExePath);
        return true;
    }

    private static bool WaitForHubWindow(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            using var automation = new UIA3Automation();
            var found = automation.GetDesktop().FindAllChildren()
                .Any(e => SafeName(e) == HubWindowTitle);
            if (found) return true;
            Thread.Sleep(500);
        }
        return false;
    }

    private static int? WaitForNewPSlideProcess(HashSet<int> beforePids, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var newPid = Process.GetProcessesByName(PSlideProcessName)
                .Select(p => p.Id)
                .FirstOrDefault(id => !beforePids.Contains(id));
            if (newPid != 0) return newPid;
            Thread.Sleep(500);
        }
        return null;
    }

    private static bool WaitForHomeTabReadyByPid(int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var hwnd = MainWindowHandle(pid);
            if (hwnd != IntPtr.Zero)
            {
                using var automation = new UIA3Automation();
                try
                {
                    var window = automation.FromHandle(hwnd);
                    if (window.FindFirstDescendant(cf => cf.ByName("새 슬라이드")) is not null)
                        return true;
                }
                catch
                {
                    // window still settling (e.g. not yet fully registered with UIA) - keep polling
                }
            }
            Thread.Sleep(500);
        }
        return false;
    }

    private static IntPtr MainWindowHandle(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Refresh();
            return process.MainWindowHandle;
        }
        catch (ArgumentException)
        {
            return IntPtr.Zero; // process already exited
        }
    }

    private static string? ReadWindowTitle(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Refresh();
            var title = process.MainWindowTitle;
            return string.IsNullOrWhiteSpace(title) ? null : title;
        }
        catch (ArgumentException)
        {
            return null; // process already exited
        }
    }

    private static string SafeName(FlaUI.Core.AutomationElements.AutomationElement e)
    {
        try { return e.Name ?? string.Empty; } catch { return string.Empty; }
    }
}
