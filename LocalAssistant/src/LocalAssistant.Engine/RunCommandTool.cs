using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalAssistant.Engine;

// CORE 게이트웨이의 exec_command를 자체 앱에 들여온 버전 (03-orchestrator.md 3.3 반전).
// AllowedFoldersStore 화이트리스트 밖에서는 구조적으로 거부 - LLM의 판단에 맡기지 않음.
public class RunCommandTool : ITool
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    public string Name => "run_command";
    public string Description =>
        "허용된 폴더 안에서 셸 명령을 실행하고 stdout/stderr/종료 코드를 반환한다. " +
        "허용된 폴더는 사용자가 'LocalAssistant.Cli addfolder <경로>'로 미리 등록해야 한다.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["command"] = new JsonObject { ["type"] = "string", ["description"] = "실행할 명령 (예: node src/cli.mjs a.pdf b.pdf)" },
            ["workingDirectory"] = new JsonObject { ["type"] = "string", ["description"] = "명령을 실행할 절대 경로 디렉터리" }
        },
        ["required"] = new JsonArray { "command", "workingDirectory" }
    };

    public async Task<string> ExecuteAsync(JsonElement input, Action<string> log)
    {
        var command = input.GetProperty("command").GetString() ?? string.Empty;
        var workingDirectory = input.GetProperty("workingDirectory").GetString() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(command))
            return "오류: command가 비어 있습니다.";

        if (!AllowedFoldersStore.IsAllowed(workingDirectory))
        {
            return $"거부됨: \"{workingDirectory}\"는 허용된 폴더가 아닙니다. " +
                   "먼저 터미널에서 'LocalAssistant.Cli addfolder \"{workingDirectory}\"'를 실행해 등록하세요.";
        }

        log($"실행 중: {command} (작업 폴더: {workingDirectory})");

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c {command}",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(Timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 이미 종료됐을 수 있음 */ }
            return $"타임아웃({Timeout.TotalSeconds}초)으로 강제 종료했습니다.\n지금까지 출력:\n{stdout}{stderr}";
        }

        return $"exit code: {process.ExitCode}\nstdout:\n{stdout}\nstderr:\n{stderr}";
    }
}
