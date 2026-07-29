using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LuckyLilliaDesktop.Utils;

/// <summary>
/// 对 schtasks.exe 的最小封装。只负责可靠执行命令和返回可诊断结果，
/// 不包含应用层的自启状态与迁移逻辑。
/// </summary>
internal static class ScheduledTaskClient
{
    public const int MaxTaskCommandLength = 262;

    private const int ProcessTimeoutMilliseconds = 10_000;
    private const int StreamDrainTimeoutMilliseconds = 2_000;
    private const int MaxDiagnosticLength = 2_000;

    // 使用 /HResult 后，任务不存在通常返回 HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND/PATH_NOT_FOUND)。
    private const int HResultFileNotFound = unchecked((int)0x80070002);
    private const int HResultPathNotFound = unchecked((int)0x80070003);

    public static ScheduledTaskCommandResult CreateLogonTask(
        string taskName,
        string executablePath,
        TimeSpan delay)
    {
        var totalMinutes = checked((int)delay.TotalMinutes);
        var seconds = delay.Seconds;
        var formattedDelay = $"{totalMinutes:0000}:{seconds:00}";

        return RunSchtasks(
            "/Create",
            "/TN", taskName,
            "/TR", executablePath,
            "/SC", "ONLOGON",
            "/RL", "LIMITED",
            "/IT",
            "/F",
            "/DELAY", formattedDelay,
            "/HResult");
    }

    public static ScheduledTaskCommandResult DeleteTask(string taskName)
    {
        // /HResult 仅受 schtasks /Create 和 /Query 支持，/Delete 不接受该参数。
        return RunSchtasks("/Delete", "/TN", taskName, "/F");
    }

    public static ScheduledTaskCommandResult QueryTaskXml(string taskName) =>
        RunSchtasks("/Query", "/TN", taskName, "/XML", "/HResult");

    public static string DescribeFailure(string operation, ScheduledTaskCommandResult result)
    {
        if (result.Status == ScheduledTaskCommandStatus.TimedOut)
            return $"{operation}：系统命令执行超时。";

        var detail = !string.IsNullOrWhiteSpace(result.Error)
            ? result.Error.Trim()
            : result.Output.Trim();

        if (detail.Length > MaxDiagnosticLength)
            detail = detail[..MaxDiagnosticLength] + "…";

        var exitCode = $"0x{unchecked((uint)result.ExitCode):X8}";
        return string.IsNullOrWhiteSpace(detail)
            ? $"{operation}（退出代码：{exitCode}）。"
            : $"{operation}（退出代码：{exitCode}）：{detail}";
    }

    private static ScheduledTaskCommandResult RunSchtasks(params string[] arguments)
    {
        var schtasksPath = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
        if (!File.Exists(schtasksPath))
        {
            return new ScheduledTaskCommandResult(
                ScheduledTaskCommandStatus.Failed,
                -1,
                string.Empty,
                $"系统中未找到 schtasks.exe：{schtasksPath}");
        }

        try
        {
            var processResult = RunProcess(schtasksPath, arguments);
            var status = processResult switch
            {
                { TimedOut: true } => ScheduledTaskCommandStatus.TimedOut,
                { ExitCode: 0 } => ScheduledTaskCommandStatus.Success,
                _ when IsNotFoundExitCode(processResult.ExitCode) => ScheduledTaskCommandStatus.NotFound,
                _ => ScheduledTaskCommandStatus.Failed
            };

            return new ScheduledTaskCommandResult(
                status,
                processResult.ExitCode,
                processResult.Output,
                processResult.Error);
        }
        catch (Exception ex)
        {
            return new ScheduledTaskCommandResult(
                ScheduledTaskCommandStatus.Failed,
                -1,
                string.Empty,
                ex.Message);
        }
    }

    private static ProcessResult RunProcess(string fileName, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        process.Start();

        var outputBuffer = new MemoryStream();
        var errorBuffer = new MemoryStream();
        var outputTask = process.StandardOutput.BaseStream.CopyToAsync(outputBuffer);
        var errorTask = process.StandardError.BaseStream.CopyToAsync(errorBuffer);

        if (!process.WaitForExit(ProcessTimeoutMilliseconds))
        {
            TryTerminate(process);
            var streamsDrained = DrainStreams(outputTask, errorTask);
            ReleaseOutputBuffers(outputBuffer, errorBuffer, outputTask, errorTask, streamsDrained);
            return new ProcessResult(-1, string.Empty, string.Empty, TimedOut: true);
        }

        if (!DrainStreams(outputTask, errorTask))
        {
            ReleaseOutputBuffers(outputBuffer, errorBuffer, outputTask, errorTask, streamsDrained: false);
            return new ProcessResult(
                -1,
                string.Empty,
                "系统命令已退出，但读取命令输出超时。",
                TimedOut: false);
        }

        var output = DecodeProcessOutput(outputBuffer.ToArray());
        var error = DecodeProcessOutput(errorBuffer.ToArray());
        outputBuffer.Dispose();
        errorBuffer.Dispose();

        return new ProcessResult(process.ExitCode, output, error, TimedOut: false);
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(StreamDrainTimeoutMilliseconds);
        }
        catch
        {
            // 调用方会得到超时结果；这里不覆盖原始失败原因。
        }
    }

    private static bool DrainStreams(params Task[] streamTasks)
    {
        try
        {
            return Task.WaitAll(streamTasks, StreamDrainTimeoutMilliseconds);
        }
        catch
        {
            return false;
        }
    }

    private static void ReleaseOutputBuffers(
        MemoryStream outputBuffer,
        MemoryStream errorBuffer,
        Task outputTask,
        Task errorTask,
        bool streamsDrained)
    {
        if (streamsDrained)
        {
            outputBuffer.Dispose();
            errorBuffer.Dispose();
            return;
        }

        // CopyToAsync 仍可能正在结束。完成后再释放缓冲区，避免写入已释放流。
        _ = Task.WhenAll(outputTask, errorTask).ContinueWith(
            task =>
            {
                _ = task.Exception;
                outputBuffer.Dispose();
                errorBuffer.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal static string DecodeProcessOutput(byte[] bytes)
    {
        if (bytes.Length == 0)
            return string.Empty;

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        // schtasks /Query /XML 在部分 Windows 版本中输出无 BOM 的 UTF-16。
        if (bytes.Length >= 4 && bytes[1] == 0 && bytes[3] == 0)
            return Encoding.Unicode.GetString(bytes);

        if (bytes.Length >= 4 && bytes[0] == 0 && bytes[2] == 0)
            return Encoding.BigEndianUnicode.GetString(bytes);

        return Encoding.UTF8.GetString(bytes);
    }

    private static bool IsNotFoundExitCode(int exitCode) =>
        exitCode is 2 or 3 or HResultFileNotFound or HResultPathNotFound;

    private readonly record struct ProcessResult(
        int ExitCode,
        string Output,
        string Error,
        bool TimedOut);
}

internal enum ScheduledTaskCommandStatus
{
    Success,
    NotFound,
    Failed,
    TimedOut
}

internal readonly record struct ScheduledTaskCommandResult(
    ScheduledTaskCommandStatus Status,
    int ExitCode,
    string Output,
    string Error);
