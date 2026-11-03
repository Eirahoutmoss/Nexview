using System.Diagnostics;
using NexView.Core.Infrastructure;

namespace NexView.Core.Conversion;

public sealed record BatchProgress(int Done, int Total, int Succeeded, int Failed, int Skipped, string? Current, TimeSpan? Remaining);

public sealed class BatchReport
{
    public List<ConversionResult> Results { get; } = new();
    public int Succeeded => Results.Count(r => r.Status == ConversionStatus.Success);
    public int Failed => Results.Count(r => r.Status == ConversionStatus.Failed);
    public int Skipped => Results.Count(r => r.Status == ConversionStatus.Skipped);
    public bool Cancelled { get; set; }
    public long SourceBytes => Results.Sum(r => r.SourceBytes);
    public long OutputBytes => Results.Where(r => r.Status == ConversionStatus.Success).Sum(r => r.OutputBytes);
    public TimeSpan Elapsed { get; set; }
}

/// <summary>Duraklatılabilir eşzamansız kapı.</summary>
public sealed class PauseController
{
    private TaskCompletionSource _gate = CreateOpen();
    public bool IsPaused { get; private set; }

    public void Pause()
    {
        if (IsPaused) return;
        IsPaused = true;
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Resume()
    {
        if (!IsPaused) return;
        IsPaused = false;
        _gate.TrySetResult();
    }

    public Task WaitAsync(CancellationToken ct) => _gate.Task.WaitAsync(ct);

    private static TaskCompletionSource CreateOpen()
    {
        var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.SetResult();
        return t;
    }
}

/// <summary>Toplu dönüştürme kuyruğu: hatalı dosya kuyruğu durdurmaz, duraklat/devam/iptal destekler.</summary>
public sealed class BatchRunner(IImageConverter converter)
{
    public async Task<BatchReport> RunAsync(IReadOnlyList<string> files, ConversionOptions options, OutputOptions output,
        IProgress<BatchProgress>? progress, PauseController? pause, CancellationToken ct, int? parallelism = null)
    {
        var report = new BatchReport();
        var sw = Stopwatch.StartNew();
        int total = files.Count, done = 0, ok = 0, failed = 0, skipped = 0;
        var degree = parallelism ?? Math.Clamp(Environment.ProcessorCount / 3, 1, 4);
        using var gate = new SemaphoreSlim(degree);
        var lockObj = new object();

        progress?.Report(new BatchProgress(0, total, 0, 0, 0, null, null));
        var tasks = new List<Task>();
        try
        {
            foreach (var file in files)
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                if (pause != null) await pause.WaitAsync(ct).ConfigureAwait(false);
                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        ConversionResult r;
                        try { r = converter.Convert(file, options, output, ct); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Log.Warn($"Toplu dönüştürme hatası: {file}", ex);
                            r = new ConversionResult(file, null, ConversionStatus.Failed, ex.Message);
                        }
                        lock (lockObj)
                        {
                            report.Results.Add(r);
                            done++;
                            if (r.Status == ConversionStatus.Success) ok++;
                            else if (r.Status == ConversionStatus.Failed) failed++;
                            else skipped++;
                            TimeSpan? eta = done > 0 ? TimeSpan.FromTicks(sw.Elapsed.Ticks / done * (total - done)) : null;
                            progress?.Report(new BatchProgress(done, total, ok, failed, skipped, Path.GetFileName(file), eta));
                        }
                    }
                    finally { gate.Release(); }
                }, CancellationToken.None));
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            report.Cancelled = true;
            try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }
        }
        report.Elapsed = sw.Elapsed;
        return report;
    }
}
