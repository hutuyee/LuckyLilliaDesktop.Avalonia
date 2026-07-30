using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace LuckyLilliaDesktop.Utils;

/// <summary>
/// 任务计划程序 XML 中与开机自启有关的最小定义。
/// 与系统调用分离，便于在非 Windows 环境中测试解析逻辑。
/// </summary>
internal readonly record struct StartupTaskDefinition
{
    private readonly bool _taskEnabled;
    private readonly bool _hasEnabledLogonTrigger;
    private readonly bool _usesLimitedRunLevel;
    private readonly bool _usesInteractiveLogon;
    private readonly string? _executablePath;

    public StartupTaskDefinition(
        bool taskEnabled,
        bool hasEnabledLogonTrigger,
        bool usesLimitedRunLevel,
        bool usesInteractiveLogon,
        string? executablePath)
    {
        _taskEnabled = taskEnabled;
        _hasEnabledLogonTrigger = hasEnabledLogonTrigger;
        _usesLimitedRunLevel = usesLimitedRunLevel;
        _usesInteractiveLogon = usesInteractiveLogon;
        _executablePath = executablePath;
    }

    public bool IsSafeLogonTask =>
        _taskEnabled &&
        _hasEnabledLogonTrigger &&
        _usesLimitedRunLevel &&
        _usesInteractiveLogon;

    public bool TargetsExecutable(string expectedExecutablePath)
    {
        return IsSafeLogonTask &&
               StartupTaskDefinitionParser.PathsEqual(_executablePath, expectedExecutablePath);
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
        return TryGetCommandLineExecutablePath(commandLine, out var executablePath) &&
               PathsEqual(executablePath, expectedExecutablePath);
    }

    public static bool TryGetCommandLineExecutablePath(
        string? commandLine,
        out string executablePath)
    {
        executablePath = string.Empty;
        if (string.IsNullOrWhiteSpace(commandLine))
            return false;

        var trimmed = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        string candidate;

        if (trimmed.StartsWith('"'))
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            if (closingQuote <= 1 ||
                (closingQuote + 1 < trimmed.Length && !char.IsWhiteSpace(trimmed[closingQuote + 1])))
            {
                return false;
            }

            candidate = trimmed[1..closingQuote];
        }
        else
        {
            // 旧版值可能没有引号且路径中含空格。以第一个后接空白或行尾的 .exe
            // 作为可执行文件边界，避免直接按空格切割路径。
            var searchStart = 0;
            var executableEnd = -1;
            while (searchStart < trimmed.Length)
            {
                var extensionIndex = trimmed.IndexOf(
                    ".exe",
                    searchStart,
                    StringComparison.OrdinalIgnoreCase);
                if (extensionIndex < 0)
                    break;

                var candidateEnd = extensionIndex + 4;
                if (candidateEnd == trimmed.Length || char.IsWhiteSpace(trimmed[candidateEnd]))
                {
                    executableEnd = candidateEnd;
                    break;
                }

                searchStart = candidateEnd;
            }

            if (executableEnd <= 0)
                return false;

            candidate = trimmed[..executableEnd];
        }

        executablePath = NormalizeExecutablePath(candidate) ?? string.Empty;
        return executablePath.Length > 0;
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
