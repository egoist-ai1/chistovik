using System.Text;
using System.Text.Json;

namespace Sandbox.Live;

public enum Status { Pass, Fail, Skip }

public sealed record CheckResult(string Scenario, string Name, string Status, string Detail);

/// <summary>Итоги проверок: PASS / FAIL / ПРОПУСК. Сохраняется в results.json и results.md после каждого сценария.</summary>
public sealed class Report(string dir)
{
    readonly List<CheckResult> _items = [];
    readonly object _gate = new();

    public int FailCount { get { lock (_gate) return _items.Count(i => i.Status == "FAIL"); } }

    public void Add(string scenario, string name, Status s, string detail = "")
    {
        var text = s switch { Status.Pass => "PASS", Status.Fail => "FAIL", _ => "ПРОПУСК" };
        lock (_gate) _items.Add(new CheckResult(scenario, name, text, detail));
        Console.WriteLine($"[{text}] {scenario} / {name}" + (detail.Length > 0 ? ": " + detail : ""));
    }

    public void Pass(string sc, string name, string detail = "") => Add(sc, name, Status.Pass, detail);
    public void Fail(string sc, string name, string detail = "") => Add(sc, name, Status.Fail, detail);
    public void Skip(string sc, string name, string detail = "") => Add(sc, name, Status.Skip, detail);

    public void Check(string sc, string name, bool ok, string detailOk = "", string detailFail = "")
        => Add(sc, name, ok ? Status.Pass : Status.Fail, ok ? detailOk : detailFail);

    public void Save()
    {
        CheckResult[] snap;
        lock (_gate) snap = [.. _items];
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "results.json"),
            JsonSerializer.Serialize(new
            {
                machine = Environment.MachineName,
                finishedUtc = DateTimeOffset.UtcNow,
                pass = snap.Count(i => i.Status == "PASS"),
                fail = snap.Count(i => i.Status == "FAIL"),
                skip = snap.Count(i => i.Status == "ПРОПУСК"),
                checks = snap,
            }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

        var sb = new StringBuilder();
        sb.AppendLine("# Результаты живого стенда Чистовика");
        sb.AppendLine();
        sb.AppendLine($"PASS: {snap.Count(i => i.Status == "PASS")}, FAIL: {snap.Count(i => i.Status == "FAIL")}, ПРОПУСК: {snap.Count(i => i.Status == "ПРОПУСК")}");
        sb.AppendLine();
        sb.AppendLine("| Сценарий | Проверка | Итог | Подробности |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var i in snap)
            sb.AppendLine($"| {Cell(i.Scenario)} | {Cell(i.Name)} | {i.Status} | {Cell(i.Detail)} |");
        File.WriteAllText(Path.Combine(dir, "results.md"), sb.ToString(), new UTF8Encoding(true));
    }

    static string Cell(string s) => s.Replace("|", "/").Replace("\r", " ").Replace("\n", " ");
}
