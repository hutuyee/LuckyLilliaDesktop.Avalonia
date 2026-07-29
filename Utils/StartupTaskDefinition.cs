using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace LuckyLilliaDesktop.Utils;

/// <summary>
/// 任务计划程序 XML 中与开机自启有关的最小定义。
/// 与系统调用分离，便于在非 Windows 环境中测试解析逻辑。
/// </summary>
internal readonly record struct StartupTaskDefinition(
    bool TaskEnabled,
    bool HasEnabledLogonTrigger,
    bool UsesLimitedRunLevel,
    bool UsesInteractiveLogon,
    string? ExecutablePath)
{
    public bool TargetsExecutable(string expectedExecutablePath)
    {
        return TaskEnabled &&
               HasEnabledLogonTrigger &&
               UsesLimitedRunLevel &&
               UsesInteractiveLogon &&
               StartupTaskDefinitionParser.PathsEqual(ExecutablePath, expectedExecutablePath);
    }
}

internal static class StartupTaskDefinitionParser
{
    public static bool TryParse(
        string xml,
        out StartupTaskDefinition definition,
        out string errorMessage)
    {
        definition = default;
        errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(xml))
        {
            errorMessage = "任务计划程序返回了空的任务定义。";
            return false;
        }

        try
        {
            var document = XDocument.Parse(xml.TrimStart((char)0xFEFF), LoadOptions.None);
            var root = document.Root;
            if (root == null || !string.Equals(root.Name.LocalName, "Task", StringComparison.Ordinal))
            {
                errorMessage = "任务定义缺少有效的 Task 根节点。";
                return false;
            }

            var ns = root.Name.Namespace;
            var taskEnabled = ReadEnabled(
                root.Element(ns + "Settings")?.Element(ns + "Enabled"),
                defaultValue: true);

            var hasEnabledLogonTrigger = root
                .Descendants(ns + "LogonTrigger")
                .Any(trigger => ReadEnabled(trigger.Element(ns + "Enabled"), defaultValue: true));

            var actions = root.Element(ns + "Actions");
            var actionContext = actions?.Attribute("Context")?.Value;
            var principals = root
                .Element(ns + "Principals")?
                .Elements(ns + "Principal")
                .ToArray() ?? [];
            var principal = string.IsNullOrWhiteSpace(actionContext)
                ? principals.FirstOrDefault()
                : principals.FirstOrDefault(candidate =>
                    string.Equals(
                        candidate.Attribute("id")?.Value,
                        actionContext,
                        StringComparison.Ordinal));
            var principalContextIsValid = principals.Length == 0 || principal != null;

            var usesLimitedRunLevel = principalContextIsValid && ReadOptionalEnum(
                principal?.Element(ns + "RunLevel"),
                "LeastPrivilege",
                "0");
            var usesInteractiveLogon = principalContextIsValid && ReadOptionalEnum(
                principal?.Element(ns + "LogonType"),
                "InteractiveToken");

            var executablePath = actions?
                .Elements(ns + "Exec")
                .Select(exec => exec.Element(ns + "Command")?.Value)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

            definition = new StartupTaskDefinition(
                taskEnabled,
                hasEnabledLogonTrigger,
                usesLimitedRunLevel,
                usesInteractiveLogon,
                NormalizeExecutablePath(executablePath));
            return true;
        }
        catch (System.Xml.XmlException ex)
        {
            errorMessage = $"任务定义 XML 无法解析：{ex.Message}";
            return false;
        }
    }

    public static bool CommandLineTargetsExecutable(string? commandLine, string expectedExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || string.IsNullOrWhiteSpace(expectedExecutablePath))
            return false;

        var trimmed = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        if (trimmed.StartsWith('"'))
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            if (closingQuote <= 1 ||
                (closingQuote + 1 < trimmed.Length && !char.IsWhiteSpace(trimmed[closingQuote + 1])))
            {
                return false;
            }

            return PathsEqual(trimmed[1..closingQuote], expectedExecutablePath);
        }

        // 旧版值有时没有引号。先把整个值当作路径，再按预期 EXE 长度识别附带参数的情况，
        // 避免直接按空格切割导致中文或空格目录被误判。
        if (PathsEqual(trimmed, expectedExecutablePath))
            return true;

        var normalizedExpected = NormalizeExecutablePath(expectedExecutablePath);
        if (string.IsNullOrWhiteSpace(normalizedExpected) ||
            trimmed.Length < normalizedExpected.Length ||
            !trimmed.StartsWith(normalizedExpected, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return trimmed.Length == normalizedExpected.Length ||
               char.IsWhiteSpace(trimmed[normalizedExpected.Length]);
    }

    public static bool PathsEqual(string? left, string? right)
    {
        var normalizedLeft = NormalizeExecutablePath(left);
        var normalizedRight = NormalizeExecutablePath(right);

        return !string.IsNullOrWhiteSpace(normalizedLeft) &&
               !string.IsNullOrWhiteSpace(normalizedRight) &&
               string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ReadEnabled(XElement? element, bool defaultValue)
    {
        if (element == null)
            return defaultValue;

        var value = element.Value.Trim();
        if (bool.TryParse(value, out var parsed))
            return parsed;

        return value switch
        {
            "1" => true,
            "0" => false,
            _ => false
        };
    }

    private static bool ReadOptionalEnum(XElement? element, params string[] acceptedValues)
    {
        if (element == null)
            return true;

        var value = element.Value.Trim();
        return acceptedValues.Any(accepted =>
            string.Equals(value, accepted, StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizeExecutablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var trimmed = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (trimmed.Length == 0)
            return null;

        try
        {
            // 测试可能在非 Windows 主机运行。此时 Path.GetFullPath 会把 C:\... 当成相对路径，
            // 因此保留 Windows 绝对路径的语义，只统一目录分隔符。
            if (!OperatingSystem.IsWindows() && LooksLikeWindowsAbsolutePath(trimmed))
                return trimmed.Replace('/', '\\').TrimEnd('\\');

            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        }
        catch
        {
            return trimmed;
        }
    }

    private static bool LooksLikeWindowsAbsolutePath(string path)
    {
        return path.StartsWith(@"\\", StringComparison.Ordinal) ||
               (path.Length >= 3 &&
                char.IsAsciiLetter(path[0]) &&
                path[1] == ':' &&
                (path[2] == '\\' || path[2] == '/'));
    }
}
