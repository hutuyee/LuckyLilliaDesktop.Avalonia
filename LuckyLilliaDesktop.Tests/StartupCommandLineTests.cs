using LuckyLilliaDesktop.Utils;

namespace LuckyLilliaDesktop.Tests;

public class StartupCommandLineTests
{
    private const string CurrentExecutable =
        @"C:\Users\测试 用户\Lucky Lillia\LuckyLilliaDesktop.exe";

    [Theory]
    [InlineData("\"C:\\Users\\测试 用户\\Lucky Lillia\\LuckyLilliaDesktop.exe\"")]
    [InlineData("\"C:\\Users\\测试 用户\\Lucky Lillia\\LuckyLilliaDesktop.exe\" --startup-delay=5")]
    [InlineData("C:\\Users\\测试 用户\\Lucky Lillia\\LuckyLilliaDesktop.exe")]
    [InlineData("C:\\Users\\测试 用户\\Lucky Lillia\\LuckyLilliaDesktop.exe --startup-delay=5")]
    public void TargetsExecutable_CurrentExecutable_ReturnsTrue(string commandLine)
    {
        Assert.True(StartupCommandLine.TargetsExecutable(commandLine, CurrentExecutable));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"C:\\Other\\LuckyLilliaDesktop.exe\"")]
    [InlineData("C:\\Users\\测试 用户\\Lucky Lillia\\LuckyLilliaDesktop.exe.old")]
    [InlineData("\"C:\\Users\\测试 用户\\Lucky Lillia\\LuckyLilliaDesktop.exe")]
    [InlineData("\"C:\\Users\\测试 用户\\Lucky Lillia\\LuckyLilliaDesktop.exe\"evil")]
    public void TargetsExecutable_InvalidOrDifferentCommand_ReturnsFalse(string commandLine)
    {
        Assert.False(StartupCommandLine.TargetsExecutable(commandLine, CurrentExecutable));
    }

    [Theory]
    [InlineData(
        "\"C:\\Users\\hutuy\\Desktop\\api\\lucky-lillia-desktop.exe\" --startup-delay=5",
        "C:\\Users\\hutuy\\Desktop\\api\\lucky-lillia-desktop.exe")]
    [InlineData(
        "C:\\Users\\测试 用户\\Lucky Lillia\\LuckyLilliaDesktop.exe --startup-delay=5",
        "C:\\Users\\测试 用户\\Lucky Lillia\\LuckyLilliaDesktop.exe")]
    public void TryGetExecutablePath_ValidCommand_ExtractsPath(
        string commandLine,
        string expectedPath)
    {
        var parsed = StartupCommandLine.TryGetExecutablePath(commandLine, out var executablePath);

        Assert.True(parsed);
        Assert.True(StartupCommandLine.PathsEqual(expectedPath, executablePath));
    }

    [Theory]
    [InlineData("\"C:\\Old\\lucky-lillia-desktop.exe")]
    [InlineData("C:\\Tools\\LuckyLilliaDesktop.exe.old")]
    [InlineData("not-an-executable")]
    public void TryGetExecutablePath_InvalidCommand_ReturnsFalse(string commandLine)
    {
        Assert.False(StartupCommandLine.TryGetExecutablePath(commandLine, out _));
    }

    [Theory]
    [InlineData(
        "C:/Users/Test/LuckyLilliaDesktop.exe",
        "c:\\users\\test\\LuckyLilliaDesktop.exe")]
    public void PathsEqual_EquivalentWindowsPaths_ReturnsTrue(string left, string right)
    {
        Assert.True(StartupCommandLine.PathsEqual(left, right));
    }
}
