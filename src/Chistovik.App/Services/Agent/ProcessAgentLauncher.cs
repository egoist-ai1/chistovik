using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;

namespace Chistovik.App.Services.Agent;

/// <summary>Запускает Chistovik.Agent.exe рядом с приложением через ShellExecute runas (окно UAC).</summary>
public sealed class ProcessAgentLauncher : IAgentLauncher
{
    const int ErrorCancelled = 1223; // пользователь отказал в UAC

    readonly string _agentPath;

    public ProcessAgentLauncher(string? agentPath = null)
        => _agentPath = agentPath ?? Path.Combine(AppContext.BaseDirectory, "Chistovik.Agent.exe");

    public static string BuildArguments(AgentLaunchArgs a)
        => $"--pipe {Quote(a.PipeName)} --parent-pid {a.ParentPid} --user-sid {Quote(a.UserSid)} --app-path {Quote(a.AppPath)}";

    static string Quote(string s) => "\"" + s.Replace("\"", "") + "\"";

    public Task<AgentLaunchResult> LaunchAsync(AgentLaunchArgs args, CancellationToken ct)
    {
        if (!File.Exists(_agentPath))
            return Task.FromResult(new AgentLaunchResult(AgentLaunchStatus.Failed, "Не найден Chistovik.Agent.exe рядом с приложением."));

        // Окно UAC блокирует поток, поэтому запуск уходит в пул.
        return Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo(_agentPath, BuildArguments(args))
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = Path.GetDirectoryName(_agentPath) ?? AppContext.BaseDirectory,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                using var p = Process.Start(psi);
                return new AgentLaunchResult(AgentLaunchStatus.Started);
            }
            catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
            {
                return new AgentLaunchResult(AgentLaunchStatus.Declined);
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException)
            {
                return new AgentLaunchResult(AgentLaunchStatus.Failed, e.Message);
            }
        }, ct);
    }
}

public sealed class NamedPipeAgentTransport : IAgentTransport
{
    public async Task<Stream> ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds, ct).ConfigureAwait(false);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
