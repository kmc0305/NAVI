using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalAssistant.Engine.Tools;

// Wraps the M1 pipeline (PolarisLauncher + PdfToPptxJob) as a Claude tool.
// The progress file path is derived from the chrome title hint so different
// PDFs never collide into the same job, and the same hint naturally resumes
// an interrupted job instead of starting a fresh blank presentation.
public class ConvertChromePdfToPptxTool : ITool
{
    public string Name => "convert_chrome_pdf_to_pptx";

    public string Description =>
        "크롬 창에 PDF로 열려 있는 문서를 Polaris Office PPTX 프레젠테이션으로 변환한다. " +
        "Polaris Office는 자동으로 실행되고 빈 프레젠테이션이 자동으로 생성된다. " +
        "chrome_window_title_hint로 어떤 크롬 창인지 구분할 수 있는 탭 제목의 일부(예: 파일명 일부)가 꼭 필요하다 - " +
        "사용자 발화에서 알 수 없으면 도구를 호출하기 전에 사용자에게 먼저 물어봐라.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["chrome_window_title_hint"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "PDF가 열려 있는 크롬 창 제목(탭 제목)에 포함된 구분용 문자열"
            }
        },
        ["required"] = new JsonArray("chrome_window_title_hint")
    };

    public Task<string> ExecuteAsync(JsonElement input, Action<string> log)
    {
        var hint = input.GetProperty("chrome_window_title_hint").GetString();
        if (string.IsNullOrWhiteSpace(hint))
            return Task.FromResult("chrome_window_title_hint가 비어 있어서 실행할 수 없습니다.");

        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalAssistant", "jobs");
        Directory.CreateDirectory(dataDir);
        var progressPath = Path.Combine(dataDir, $"pdf_job_{ShortHash(hint)}.json");

        var result = PdfToPptxJob.Run(hint, progressPath, log);

        return Task.FromResult(
            $"결과: {result.Outcome}, 완료 {result.LastCompletedPage}/{result.TotalPages} 페이지");
    }

    private static string ShortHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }
}
