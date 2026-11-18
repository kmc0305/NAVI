using System.Text.Json;

namespace LocalAssistant.Engine;

public class JobProgress
{
    public int LastCompletedPage { get; set; }
    public int TotalPages { get; set; }

    // Title of the PSlide.exe window this job is pasting into - set once a blank
    // presentation is auto-created, so a resumed run reuses that exact window
    // instead of creating a fresh (and separately blank) one each time.
    public string? PolarisWindowTitle { get; set; }

    public static JobProgress Load(string path)
    {
        if (!File.Exists(path))
            return new JobProgress();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<JobProgress>(json) ?? new JobProgress();
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(path, JsonSerializer.Serialize(this));
    }
}
