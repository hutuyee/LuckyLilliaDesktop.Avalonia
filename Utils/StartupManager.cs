using Microsoft.Win32;
using System;
using System.IO;
using System.Runtime.Versioning;

namespace LuckyLilliaDesktop.Utils;

/// <summary>
/// 管理 Windows 当前用户的登录自启。
/// 优先使用任务计划程序；权限受限时回退到 HKCU Run，不要求管理员权限。
/// </summary>
public static class StartupManager
{
    private const string TaskName = "LuckyLilliaDesktop";
    private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const int MaxRegistryCommandLength = 260;
    internal const string RegistryStartupDelayArgument = "--startup-delay=5";

    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);
    private static readonly object SyncRoot = new();

    /// <summary>
    /// 检查当前登录自启状态。计划任务或当前用户注册表启动项任意一个有效即视为启用。
    /// </summary>
    public static bool IsStartupEnabled()
    {
        if (!PlatformHelper.IsWindows || !TryGetCurrentExecutablePath(out var exePath, out _))
            return false;

        try
        {
            lock (SyncRoot)
            {
                // 注册表后备不依赖任务计划服务，优先检查可避免权限受限环境中的无效等待。
                if (IsRegistryStartupEnabled(exePath))
                    return true;

                return InspectScheduledTask(exePath).IsValidForCurrentExecutable;
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
            return StartupOperationResult.Failed($"创建开机自启配置时发生异常：{ex.Message}");
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
                var usesRegistryFallback = TryGetCurrentExecutablePath(out var exePath, out _) &&
                                           IsRegistryStartupEnabled(exePath);

                if (usesRegistryFallback)
                {
                    var cleanup = CleanupStartupRegistry();
                    if (!cleanup.Success)
                        return cleanup;

                    // 注册表后备是当前生效来源。计划任务清理失败时仍保持“已关闭”，
                    // 但明确提示用户可能存在无权管理的外部任务。
                    var taskCleanup = DeleteScheduledTask();
                    return taskCleanup.Success
                        ? StartupOperationResult.Succeeded()
                        : StartupOperationResult.Succeeded(
                            "已关闭当前用户注册表启动项，但无法确认计划任务是否已清理。" +
                            taskCleanup.ErrorMessage);
                }

                var deleteResult = DeleteScheduledTask();
                if (!deleteResult.Success)
                    return deleteResult;

                return CleanupStartupRegistry();
            }
        }
        catch (Exception ex)
        {
            return StartupOperationResult.Failed($"删除开机自启配置时发生异常：{ex.Message}");
        }
    }

    /// <summary>
    /// 保留旧版公开入口。现在 HKCU Run 同时作为任务计划程序不可用时的正式后备方案。
    /// </summary>
    public static void MigrateFromLegacyRegistry() => _ = TryMigrateFromLegacyRegistry();

    internal static StartupOperationResult TryMigrateFromLegacyRegistry()
    {
        if (!PlatformHelper.IsWindows)
            return StartupOperationResult.Succeeded();

        try
        {
            lock (SyncRoot)
            {
                if (!TryReadStartupRegistryValue(out var registryValue, out var registryError))
                    return StartupOperationResult.Failed(registryError);

                if (string.IsNullOrWhiteSpace(registryValue))
                    return StartupOperationResult.Succeeded();

                if (!TryGetCurrentExecutablePath(out var exePath, out var pathError))
                    return StartupOperationResult.Failed(pathError);

                // 指向当前程序的旧值仍然有效，不再强制迁移到可能需要更高权限的计划任务。
                if (StartupTaskDefinitionParser.CommandLineTargetsExecutable(registryValue, exePath))
                    return StartupOperationResult.Succeeded();

                var cleanup = CleanupStartupRegistry();
                return cleanup.Success
                    ? StartupOperationResult.Succeeded("已清理指向旧程序路径的开机自启项。")
                    : cleanup;
            }
        }
        catch (Exception ex)
        {
            return StartupOperationResult.Failed($"检查旧版开机自启配置时发生异常：{ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static StartupOperationResult TryEnableStartupCore()
    {
        if (!TryGetCurrentExecutablePath(out var exePath, out var pathError))
            return StartupOperationResult.Failed(pathError);

        // 已经通过注册表后备启用时，不要在每次保存或启动时重复触发 schtasks。
        if (IsRegistryStartupEnabled(exePath))
            return StartupOperationResult.Succeeded();

        var existingTask = InspectScheduledTask(exePath);
        if (existingTask.IsValidForCurrentExecutable)
            return CompleteRegistryCleanup();

        string schedulerFailure;
        if (exePath.Length > ScheduledTaskClient.MaxTaskCommandLength)
        {
            schedulerFailure =
                $"当前程序路径超过任务计划程序允许的 {ScheduledTaskClient.MaxTaskCommandLength} 个字符。";
        }
        else
        {
            var createResult = ScheduledTaskClient.CreateLogonTask(TaskName, exePath, StartupDelay);
            if (createResult.Status == ScheduledTaskCommandStatus.Success)
            {
                var inspection = InspectScheduledTask(exePath);
                if (inspection.IsValidForCurrentExecutable)
                    return CompleteRegistryCleanup();

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

                schedulerFailure = reason;
            }
            else
            {
                schedulerFailure = ScheduledTaskClient.DescribeFailure(
                    "创建计划任务失败",
                    createResult);
            }
        }

        return EnableRegistryFallback(exePath, schedulerFailure);
    }

    [SupportedOSPlatform("windows")]
    private static StartupOperationResult CompleteRegistryCleanup()
    {
        var cleanup = CleanupStartupRegistry();
        return cleanup.Success
            ? StartupOperationResult.Succeeded()
            : StartupOperationResult.Succeeded(
                "计划任务已生效，但旧的注册表启动项无法清理，登录时可能重复启动。" +
                cleanup.ErrorMessage);
    }

    [SupportedOSPlatform("windows")]
    private static StartupOperationResult EnableRegistryFallback(
        string executablePath,
        string schedulerFailure)
    {
        var startupCommand = $"\"{executablePath}\" {RegistryStartupDelayArgument}";
        if (startupCommand.Length > MaxRegistryCommandLength)
        {
            return StartupOperationResult.Failed(
                $"{schedulerFailure} 当前程序路径也超过注册表启动命令允许的 " +
                $"{MaxRegistryCommandLength} 个字符，请把程序移动到更短的目录后重试。");
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(StartupRegistryPath, writable: true);
            if (key == null)
            {
                return StartupOperationResult.Failed(
                    $"{schedulerFailure} 无法打开当前用户的开机启动注册表项。");
            }

            var previousValue = key.GetValue(
                TaskName,
                defaultValue: null,
                RegistryValueOptions.DoNotExpandEnvironmentNames);
            var previousKind = previousValue == null
                ? (RegistryValueKind?)null
                : key.GetValueKind(TaskName);

            try
            {
                key.SetValue(TaskName, startupCommand, RegistryValueKind.String);
                var writtenValue = key.GetValue(
                    TaskName,
                    defaultValue: null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames) as string;

                if (!StartupTaskDefinitionParser.CommandLineTargetsExecutable(
                        writtenValue,
                        executablePath))
                {
                    throw new InvalidOperationException("写入后的注册表启动命令校验失败。");
                }
            }
            catch
            {
                RestoreRegistryValue(key, previousValue, previousKind);
                throw;
            }

            return StartupOperationResult.Succeeded(
                $"{schedulerFailure} 已自动改用当前用户注册表启动项，不需要管理员权限。");
        }
        catch (Exception ex)
        {
            return StartupOperationResult.Failed(
                $"{schedulerFailure} 注册表后备方案也失败：{ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RestoreRegistryValue(
        RegistryKey key,
        object? previousValue,
        RegistryValueKind? previousKind)
    {
        try
        {
            if (previousValue == null || previousKind == null)
                key.DeleteValue(TaskName, throwOnMissingValue: false);
            else
                key.SetValue(TaskName, previousValue, previousKind.Value);
        }
        catch
        {
            // 保留原始写入异常；恢复失败会由最终状态校验和日志暴露。
        }
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
    private static bool IsRegistryStartupEnabled(string executablePath)
    {
        return TryReadStartupRegistryValue(out var value, out _) &&
               StartupTaskDefinitionParser.CommandLineTargetsExecutable(value, executablePath);
    }

    [SupportedOSPlatform("windows")]
    private static bool TryReadStartupRegistryValue(out string? value, out string errorMessage)
    {
        value = null;
        errorMessage = string.Empty;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, writable: false);
            value = key?.GetValue(
                TaskName,
                defaultValue: null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"读取开机自启注册表项失败：{ex.Message}";
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static StartupOperationResult CleanupStartupRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, writable: true);
            key?.DeleteValue(TaskName, throwOnMissingValue: false);
            return StartupOperationResult.Succeeded();
        }
        catch (Exception ex)
        {
            return StartupOperationResult.Failed($"清理开机自启注册表项失败：{ex.Message}");
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
