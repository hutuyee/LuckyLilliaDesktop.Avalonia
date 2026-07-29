using Microsoft.Win32;
using System;
using System.IO;
using System.Runtime.Versioning;

namespace LuckyLilliaDesktop.Utils;

/// <summary>
/// 管理 Windows 当前用户的登录自启。
/// </summary>
public static class StartupManager
{
    private const string TaskName = "LuckyLilliaDesktop";
    private const string LegacyRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);
    private static readonly object SyncRoot = new();

    /// <summary>
    /// 检查当前登录自启状态，并在发现旧版注册表配置时完成安全迁移。
    /// </summary>
    public static bool IsStartupEnabled()
    {
        if (!PlatformHelper.IsWindows || !TryGetCurrentExecutablePath(out var exePath, out _))
            return false;

        try
        {
            // 任务检查与旧配置迁移必须在同一临界区内完成；否则并发关闭时，
            // 已等待的迁移操作可能在关闭后重新创建自启任务。
            lock (SyncRoot)
            {
                var task = InspectScheduledTask(exePath);
                if (task.IsValidForCurrentExecutable)
                    return true;

                if (!TryReadLegacyRegistryValue(out var registryValue, out _) ||
                    string.IsNullOrWhiteSpace(registryValue))
                {
                    return false;
                }

                // 配置页可能与启动时的后台迁移并发读取。这里在同一把锁内补做迁移，
                // 确保返回值不会落后于随后创建出的计划任务。
                var migration = TryEnableStartupCore();
                return migration.Success ||
                       StartupTaskDefinitionParser.CommandLineTargetsExecutable(registryValue, exePath);
            }
        }
        catch
        {
            return false;
        }
    }

    public static bool EnableStartup() => TryEnableStartup().Success;

    internal static StartupOperationResult TryEnableStartup()
    {
        if (!PlatformHelper.IsWindows)
            return StartupOperationResult.Failed("当前平台不支持 Windows 开机自启。");

        try
        {
            lock (SyncRoot)
            {
                return TryEnableStartupCore();
            }
        }
        catch (Exception ex)
        {
            return StartupOperationResult.Failed($"创建开机自启任务时发生异常：{ex.Message}");
        }
    }

    public static bool DisableStartup() => TryDisableStartup().Success;

    internal static StartupOperationResult TryDisableStartup()
    {
        if (!PlatformHelper.IsWindows)
            return StartupOperationResult.Failed("当前平台不支持 Windows 开机自启。");

        try
        {
            lock (SyncRoot)
            {
                var deleteResult = DeleteScheduledTask();
                if (!deleteResult.Success)
                    return deleteResult;

                return CleanupLegacyRegistry();
            }
        }
        catch (Exception ex)
        {
            return StartupOperationResult.Failed($"删除开机自启配置时发生异常：{ex.Message}");
        }
    }

    /// <summary>
    /// 迁移旧版 HKCU Run 配置。保留原有公开入口以兼容现有调用方。
    /// </summary>
    public static void MigrateFromLegacyRegistry() => _ = TryMigrateFromLegacyRegistry();

    /// <summary>
    /// 只有新任务创建并验证成功后才删除旧值，并返回可记录的诊断信息。
    /// </summary>
    internal static StartupOperationResult TryMigrateFromLegacyRegistry()
    {
        if (!PlatformHelper.IsWindows)
            return StartupOperationResult.Succeeded();

        try
        {
            lock (SyncRoot)
            {
                if (!TryReadLegacyRegistryValue(out var registryValue, out var registryError))
                    return StartupOperationResult.Failed(registryError);

                return string.IsNullOrWhiteSpace(registryValue)
                    ? CleanupLegacyRegistry()
                    : TryEnableStartupCore();
            }
        }
        catch (Exception ex)
        {
            return StartupOperationResult.Failed($"迁移旧版开机自启配置时发生异常：{ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static StartupOperationResult TryEnableStartupCore()
    {
        if (!TryGetCurrentExecutablePath(out var exePath, out var pathError))
            return StartupOperationResult.Failed(pathError);

        var existingTask = InspectScheduledTask(exePath);
        if (existingTask.IsValidForCurrentExecutable)
            return CompleteRegistryCleanup("计划任务已经有效");

        if (exePath.Length > ScheduledTaskClient.MaxTaskCommandLength)
        {
            return StartupOperationResult.Failed(
                $"当前程序路径超过任务计划程序允许的 {ScheduledTaskClient.MaxTaskCommandLength} 个字符。" +
                "请把程序移动到更短的目录后重试。");
        }

        var createResult = ScheduledTaskClient.CreateLogonTask(TaskName, exePath, StartupDelay);
        if (createResult.Status != ScheduledTaskCommandStatus.Success)
        {
            return StartupOperationResult.Failed(
                ScheduledTaskClient.DescribeFailure("创建开机自启任务失败", createResult));
        }

        var inspection = InspectScheduledTask(exePath);
        if (!inspection.IsValidForCurrentExecutable)
        {
            var reason = inspection.Status switch
            {
                ScheduledTaskStatus.Missing => "创建命令成功返回，但系统中没有找到新任务。",
                ScheduledTaskStatus.Unavailable => inspection.ErrorMessage,
                _ => "新任务被禁用、缺少登录触发器，或启动路径与当前程序不一致。"
            };

            var rollback = DeleteScheduledTask();
            if (!rollback.Success)
            {
                return StartupOperationResult.Failed(
                    $"{reason} 自动回滚也失败，任务状态可能不一致。{rollback.ErrorMessage}");
            }

            return StartupOperationResult.Failed(reason);
        }

        return CompleteRegistryCleanup("计划任务已创建");
    }

    [SupportedOSPlatform("windows")]
    private static StartupOperationResult CompleteRegistryCleanup(string successMessage)
    {
        var cleanup = CleanupLegacyRegistry();
        return cleanup.Success
            ? StartupOperationResult.Succeeded()
            : StartupOperationResult.Succeeded(
                $"{successMessage}，但旧版注册表项无法清理，登录时可能重复启动。{cleanup.ErrorMessage}");
    }

    private static StartupOperationResult DeleteScheduledTask()
    {
        var result = ScheduledTaskClient.DeleteTask(TaskName);

        // /Delete 不支持 /HResult，任务在调用前后恰好消失时也可能返回普通失败码。
        // 因此无论删除命令是否成功，都以随后查询到的真实状态为准。
        var verification = InspectScheduledTask(expectedExecutablePath: null);
        if (verification.Status == ScheduledTaskStatus.Missing)
            return StartupOperationResult.Succeeded();

        if (result.Status != ScheduledTaskCommandStatus.Success)
        {
            var deleteError = ScheduledTaskClient.DescribeFailure("删除开机自启任务失败", result);
            return verification.Status == ScheduledTaskStatus.Unavailable
                ? StartupOperationResult.Failed($"{deleteError} {verification.ErrorMessage}")
                : StartupOperationResult.Failed(deleteError);
        }

        return verification.Status switch
        {
            ScheduledTaskStatus.Present => StartupOperationResult.Failed("删除命令成功返回，但开机自启任务仍然存在。"),
            _ => StartupOperationResult.Failed(verification.ErrorMessage)
        };
    }

    private static ScheduledTaskInspection InspectScheduledTask(string? expectedExecutablePath)
    {
        var result = ScheduledTaskClient.QueryTaskXml(TaskName);
        if (result.Status == ScheduledTaskCommandStatus.NotFound)
            return ScheduledTaskInspection.Missing();

        if (result.Status != ScheduledTaskCommandStatus.Success)
        {
            return ScheduledTaskInspection.Unavailable(
                ScheduledTaskClient.DescribeFailure("查询开机自启任务失败", result));
        }

        if (!StartupTaskDefinitionParser.TryParse(result.Output, out var definition, out var parseError))
            return ScheduledTaskInspection.Unavailable(parseError);

        var isValidForCurrentExecutable = expectedExecutablePath != null &&
                                          definition.TargetsExecutable(expectedExecutablePath);
        return ScheduledTaskInspection.Present(isValidForCurrentExecutable);
    }

    private static bool TryGetCurrentExecutablePath(out string exePath, out string errorMessage)
    {
        exePath = Environment.ProcessPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            errorMessage = "无法获取当前程序的可执行文件路径。";
            return false;
        }

        try
        {
            exePath = Path.GetFullPath(exePath);
            errorMessage = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"无法规范化当前程序路径：{ex.Message}";
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool TryReadLegacyRegistryValue(out string? value, out string errorMessage)
    {
        value = null;
        errorMessage = string.Empty;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LegacyRegistryPath, writable: false);
            value = key?.GetValue(TaskName) as string;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"读取旧版开机自启注册表项失败：{ex.Message}";
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static StartupOperationResult CleanupLegacyRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LegacyRegistryPath, writable: true);
            key?.DeleteValue(TaskName, throwOnMissingValue: false);
            return StartupOperationResult.Succeeded();
        }
        catch (Exception ex)
        {
            return StartupOperationResult.Failed($"清理旧版开机自启注册表项失败：{ex.Message}");
        }
    }

    private enum ScheduledTaskStatus
    {
        Missing,
        Present,
        Unavailable
    }

    private readonly record struct ScheduledTaskInspection(
        ScheduledTaskStatus Status,
        bool IsValidForCurrentExecutable,
        string ErrorMessage)
    {
        public static ScheduledTaskInspection Missing() =>
            new(ScheduledTaskStatus.Missing, false, string.Empty);

        public static ScheduledTaskInspection Present(bool isValidForCurrentExecutable) =>
            new(ScheduledTaskStatus.Present, isValidForCurrentExecutable, string.Empty);

        public static ScheduledTaskInspection Unavailable(string errorMessage) =>
            new(ScheduledTaskStatus.Unavailable, false, errorMessage);
    }
}

internal readonly record struct StartupOperationResult(
    bool Success,
    string ErrorMessage,
    string DiagnosticMessage)
{
    public static StartupOperationResult Succeeded(string diagnosticMessage = "") =>
        new(true, string.Empty, diagnosticMessage.Trim());

    public static StartupOperationResult Failed(string errorMessage) =>
        new(
            false,
            string.IsNullOrWhiteSpace(errorMessage) ? "未知错误。" : errorMessage.Trim(),
            string.Empty);
}
