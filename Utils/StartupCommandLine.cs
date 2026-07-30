using System;
using System.IO;

namespace LuckyLilliaDesktop.Utils;

/// <summary>
/// 解析 Windows Run 注册表值中的可执行文件路径。
/// </summary>
internal static class StartupCommandLine
{
    public static bool TargetsExecutable(string? commandLine, string expectedExecutablePath)
    {
        return TryGetExecutablePath(commandLine, out var executablePath) &&
               PathsEqual(executablePath, expectedExecutablePath);
    }

    public static bool TryGetExecutablePath(string? commandLine, out string executablePath)
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
            // 兼容旧版本未加引号且路径中含空格的值。以第一个后接空白或
            // 行尾的 .exe 作为边界，避免直接按空格截断路径。
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

    private static string? NormalizeExecutablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var trimmed = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (trimmed.Length == 0)
            return null;

        try
        {
            // 测试可能在非 Windows 主机运行。此时 Path.GetFullPath 会把 C:\... 当成
            // 相对路径，因此保留 Windows 绝对路径语义，只统一目录分隔符。
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
