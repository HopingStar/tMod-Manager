using Microsoft.Extensions.Hosting;
using TModManager.Core.Mods;
using TModManager.Core.Processes;

namespace TModManager.App.Hosting;

/// <summary>后台监控：每 3s 检测 tModLoader 运行→退出边沿，触发 .tmod.disabled 还原（对齐 Python _monitor_tmodloader）。</summary>
public sealed class TmlMonitorService : IHostedService, IDisposable
{
    private readonly TmlProcessService _procs;
    private readonly SpecialLoadService _special;
    private CancellationTokenSource? _cts;

    public TmlMonitorService(TmlProcessService procs, SpecialLoadService special)
    {
        _procs = procs;
        _special = special;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(() => LoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var wasRunning = _procs.IsRunning();
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { break; }
            try
            {
                var running = _procs.IsRunning();
                if (wasRunning && !running)
                    _special.RestoreAllDisabled();
                wasRunning = running;
            }
            catch
            {
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose() => _cts?.Dispose();
}
