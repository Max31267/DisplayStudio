using System.Diagnostics;
using System.Text.Json;
using MiniDisplay.Core;

namespace MiniDisplay.Windows;

public static class CodexReader
{
    public static string FindExecutable(string configured)
    {
        if (configured.Length > 0)
        {
            if (!Path.GetFileName(configured).Equals("codex.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Выберите файл codex.exe.");
            return File.Exists(configured) ? configured : throw new FileNotFoundException("Указанный codex.exe не найден.");
        }
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        { string path = Path.Combine(directory, "codex.exe"); if (File.Exists(path)) return path; }
        string bundled = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (Directory.Exists(bundled))
        {
            var candidate = Directory.EnumerateFiles(bundled, "codex.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (candidate is not null) return candidate;
        }
        throw new FileNotFoundException("Codex CLI не найден. Укажите путь к codex.exe.");
    }
    public static async Task<CodexQuota[]> Read(string configured, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var start = new ProcessStartInfo(FindExecutable(configured)) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("app-server"); start.ArgumentList.Add("--stdio");
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить Codex App Server.");
        // Drain stderr without retaining auth/configuration output. This client only reads quota data.
        var errors = Task.Run(async () => { while (await process.StandardError.ReadLineAsync() is not null) { } });
        try
        {
            process.StandardInput.AutoFlush = true;
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "mini_display", title = "Mini Display", version = "0.3.0" } } }));
            using var initialization = await Response(process, 1, timeout.Token);
            await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\",\"params\":{}}");
            await process.StandardInput.WriteLineAsync("{\"id\":2,\"method\":\"account/rateLimits/read\"}");
            using var response = await Response(process, 2, timeout.Token);
            return SourceParsers.Codex(response.RootElement);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new TimeoutException("Codex не ответил за 20 секунд."); }
        finally
        {
            process.StandardInput.Close();
            if (!process.HasExited) { try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)); } catch (TimeoutException) { process.Kill(true); } }
            await errors;
        }
    }
    private static async Task<JsonDocument> Response(Process process, int id, CancellationToken token)
    {
        while (true)
        {
            string? line = await process.StandardOutput.ReadLineAsync(token);
            if (line is null) throw new IOException("Codex завершил соединение.");
            if (line.Length > 1024 * 1024) throw new InvalidDataException("Слишком большой ответ Codex.");
            JsonDocument json; try { json = JsonDocument.Parse(line); } catch (JsonException) { continue; }
            if (!json.RootElement.TryGetProperty("id", out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int actual) || actual != id) { json.Dispose(); continue; }
            if (json.RootElement.TryGetProperty("error", out var error)) { string reason = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message) ? message.GetString() ?? "Нет доступа к лимитам Codex." : "Нет доступа к лимитам Codex."; json.Dispose(); throw new IOException(reason); }
            return json;
        }
    }
}
