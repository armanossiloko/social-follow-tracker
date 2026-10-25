using System.Diagnostics;
using System.Text;

namespace Following.Core;

/// <summary>Registers the daily run with Windows Task Scheduler (per-user task, no admin rights needed).</summary>
public static class DailySchedule
{
    public const string TaskName = "The Following - daily report";

    public static (bool Ok, string Output) Install(string executable, string dataDir, TimeOnly at)
    {
        var script = $"""
            $ErrorActionPreference = 'Stop'
            $action = New-ScheduledTaskAction -Execute {Quote(executable)} -Argument {Quote($"--data \"{dataDir}\"")} -WorkingDirectory {Quote(Path.GetDirectoryName(executable)!)}
            # Random delay so the request time isn't identical every day.
            $trigger = New-ScheduledTaskTrigger -Daily -At {Quote(at.ToString("HH:mm"))} -RandomDelay (New-TimeSpan -Minutes 30)
            # StartWhenAvailable: if the PC was off or asleep at the scheduled time, run as soon as it's back.
            $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -RunOnlyIfNetworkAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 30) -MultipleInstances IgnoreNew
            Register-ScheduledTask -TaskName {Quote(TaskName)} -Action $action -Trigger $trigger -Settings $settings -Description 'Fetches Instagram followers/following and rebuilds the report.' -Force | Out-Null
            $info = Get-ScheduledTaskInfo -TaskName {Quote(TaskName)}
            "Next run: $($info.NextRunTime)"
            """;
        return RunPowerShell(script);
    }

    public static (bool Ok, string Output) Remove() =>
        RunPowerShell($"$ErrorActionPreference = 'Stop'; Unregister-ScheduledTask -TaskName {Quote(TaskName)} -Confirm:$false");

    public static (bool Ok, string Output) Status() => RunPowerShell($$"""
        $ErrorActionPreference = 'Stop'
        $task = Get-ScheduledTask -TaskName {{Quote(TaskName)}}
        $info = Get-ScheduledTaskInfo -TaskName {{Quote(TaskName)}}
        "State:       $($task.State)"
        "Runs:        $($task.Actions[0].Execute) $($task.Actions[0].Arguments)"
        "Next run:    $($info.NextRunTime)"
        "Last run:    $(if ($info.LastRunTime.Year -gt 2000) { $info.LastRunTime } else { 'never' })"
        "Last result: $(if ($info.LastTaskResult -eq 267011) { 'has not run yet' } elseif ($info.LastTaskResult -eq 0) { 'success' } else { "exit code $($info.LastTaskResult) (see following.log)" })"
        """);

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    private static (bool Ok, string Output) RunPowerShell(string script)
    {
        if (!OperatingSystem.IsWindows())
            return (false, "Scheduling is only automated on Windows. Elsewhere, run `following daily` from cron.");
        var psi = new ProcessStartInfo("powershell.exe")
        {
            ArgumentList =
            {
                "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
                // Progress records would otherwise leak into stderr as CLIXML.
                Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference = 'SilentlyContinue'\n" + script)),
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var ok = process.ExitCode == 0;
        return (ok, (ok ? stdout.Result : stderr.Result + stdout.Result).Trim());
    }
}
