using LuckyLilliaDesktop.Utils;
using Xunit;

namespace LuckyLilliaDesktop.Tests;

public class StartupTaskDefinitionTests
{
    private const string CurrentExecutable = @"C:\Users\测试 用户\Lucky Lillia\LuckyLilliaDesktop.exe";

    [Fact]
    public void Parse_EnabledLogonTaskWithMatchingCommand_IsValid()
    {
        var xml = CreateTaskXml($"\"{CurrentExecutable}\"");

        var parsed = StartupTaskDefinitionParser.TryParse(xml, out var definition, out var error);

        Assert.True(parsed, error);
        Assert.True(definition.IsSafeLogonTask);
        Assert.True(definition.TargetsExecutable(CurrentExecutable));
    }

    [Fact]
    public void Parse_DisabledTask_IsNotValid()
    {
        var xml = CreateTaskXml(CurrentExecutable, taskEnabled: false);

        var parsed = StartupTaskDefinitionParser.TryParse(xml, out var definition, out var error);

        Assert.True(parsed, error);
        Assert.False(definition.IsSafeLogonTask);
        Assert.False(definition.TargetsExecutable(CurrentExecutable));
    }

    [Fact]
    public void Parse_DisabledLogonTrigger_IsNotValid()
    {
        var xml = CreateTaskXml(CurrentExecutable, triggerEnabled: false);

        var parsed = StartupTaskDefinitionParser.TryParse(xml, out var definition, out var error);

        Assert.True(parsed, error);
        Assert.False(definition.TargetsExecutable(CurrentExecutable));
    }

    [Fact]
    public void Parse_MissingEnabledNodes_DefaultsToEnabled()
    {
        var xml = CreateTaskXml(CurrentExecutable, includeTaskEnabled: false, includeTriggerEnabled: false);

        var parsed = StartupTaskDefinitionParser.TryParse(xml, out var definition, out var error);

        Assert.True(parsed, error);
        Assert.True(definition.TargetsExecutable(CurrentExecutable));
    }

    [Fact]
    public void Parse_WithoutLogonTrigger_IsNotValid()
    {
        var xml = CreateTaskXml(CurrentExecutable, includeLogonTrigger: false);

        var parsed = StartupTaskDefinitionParser.TryParse(xml, out var definition, out var error);

        Assert.True(parsed, error);
        Assert.False(definition.TargetsExecutable(CurrentExecutable));
    }

    [Fact]
    public void Parse_TaskPointingToOldLocation_IsNotValid()
    {
        var xml = CreateTaskXml(@"C:\Old\LuckyLilliaDesktop.exe");

        var parsed = StartupTaskDefinitionParser.TryParse(xml, out var definition, out var error);

        Assert.True(parsed, error);
        Assert.False(definition.TargetsExecutable(CurrentExecutable));
    }

    [Theory]
    [InlineData("\"C:\\Users\\测试 用户\\Lucky Lillia\\LuckyLilliaDesktop.exe\"")]
    [InlineData("\"C:\\Users\\测试 用户\\Lucky Lillia\\LuckyLilliaDesktop.exe\" --minimized")]
    [InlineData("C:\\Users\\测试 用户\\Lucky Lillia\\LuckyLilliaDesktop.exe")]
    [InlineData("C:\\Users\\测试 用户\\Lucky Lillia\\LuckyLilliaDesktop.exe --minimized")]
    public void LegacyCommand_TargetingCurrentExecutable_IsRecognized(string commandLine)
    {
        Assert.True(StartupTaskDefinitionParser.CommandLineTargetsExecutable(commandLine, CurrentExecutable));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"C:\\Other\\LuckyLilliaDesktop.exe\"")]
    [InlineData("C:\\Users\\测试 用户\\Lucky Lillia\\LuckyLilliaDesktop.exe.old")]
    [InlineData("\"C:\\Users\\测试 用户\\Lucky Lillia\\LuckyLilliaDesktop.exe")]
    [InlineData("\"C:\\Users\\测试 用户\\Lucky Lillia\\LuckyLilliaDesktop.exe\"evil")]
    public void LegacyCommand_NotTargetingCurrentExecutable_IsRejected(string commandLine)
    {
        Assert.False(StartupTaskDefinitionParser.CommandLineTargetsExecutable(commandLine, CurrentExecutable));
    }

    [Theory]
    [InlineData("HighestAvailable", "InteractiveToken")]
    [InlineData("1", "InteractiveToken")]
    [InlineData("LeastPrivilege", "Password")]
    [InlineData("LeastPrivilege", "S4U")]
    [InlineData("invalid", "InteractiveToken")]
    [InlineData("LeastPrivilege", "invalid")]
    public void Parse_UnsafePrincipal_IsNotValid(string runLevel, string logonType)
    {
        var xml = CreateTaskXml(
            CurrentExecutable,
            runLevel: runLevel,
            logonType: logonType);

        var parsed = StartupTaskDefinitionParser.TryParse(xml, out var definition, out var error);

        Assert.True(parsed, error);
        Assert.False(definition.TargetsExecutable(CurrentExecutable));
    }

    [Theory]
    [InlineData("LeastPrivilege", "InteractiveToken")]
    [InlineData("0", "InteractiveToken")]
    [InlineData(null, null)]
    public void Parse_LimitedInteractivePrincipal_IsValid(string? runLevel, string? logonType)
    {
        var xml = CreateTaskXml(
            CurrentExecutable,
            runLevel: runLevel,
            logonType: logonType);

        var parsed = StartupTaskDefinitionParser.TryParse(xml, out var definition, out var error);

        Assert.True(parsed, error);
        Assert.True(definition.TargetsExecutable(CurrentExecutable));
    }

    [Fact]
    public void Parse_ActionsReferencingUnknownPrincipal_IsNotValid()
    {
        var xml = CreateTaskXml(CurrentExecutable)
            .Replace("Context=\"Author\"", "Context=\"Missing\"");

        var parsed = StartupTaskDefinitionParser.TryParse(xml, out var definition, out var error);

        Assert.True(parsed, error);
        Assert.False(definition.TargetsExecutable(CurrentExecutable));
    }

    [Fact]
    public void Parse_ByteOrderMarkAndNumericEnabledValues_IsValid()
    {
        var xml = "\uFEFF" + CreateTaskXml(CurrentExecutable)
            .Replace("<Enabled>true</Enabled>", "<Enabled>1</Enabled>");

        var parsed = StartupTaskDefinitionParser.TryParse(xml, out var definition, out var error);

        Assert.True(parsed, error);
        Assert.True(definition.TargetsExecutable(CurrentExecutable));
    }

    [Theory]
    [InlineData("<Settings><Enabled>invalid</Enabled></Settings>")]
    [InlineData("<Triggers><LogonTrigger><Enabled>invalid</Enabled></LogonTrigger></Triggers>")]
    public void Parse_InvalidEnabledValue_IsNotValid(string replacement)
    {
        var xml = CreateTaskXml(CurrentExecutable);
        if (replacement.StartsWith("<Settings>"))
        {
            xml = System.Text.RegularExpressions.Regex.Replace(
                xml,
                "<Settings>.*?</Settings>",
                replacement,
                System.Text.RegularExpressions.RegexOptions.Singleline);
        }
        else
        {
            xml = System.Text.RegularExpressions.Regex.Replace(
                xml,
                "<Triggers>.*?</Triggers>",
                replacement,
                System.Text.RegularExpressions.RegexOptions.Singleline);
        }

        var parsed = StartupTaskDefinitionParser.TryParse(xml, out var definition, out var error);

        Assert.True(parsed, error);
        Assert.False(definition.TargetsExecutable(CurrentExecutable));
    }

    [Fact]
    public void Parse_WrongRootElement_ReturnsDiagnostic()
    {
        var parsed = StartupTaskDefinitionParser.TryParse("<NotTask />", out _, out var error);

        Assert.False(parsed);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Parse_MalformedXml_ReturnsDiagnostic()
    {
        var parsed = StartupTaskDefinitionParser.TryParse("<Task>", out _, out var error);

        Assert.False(parsed);
        Assert.NotEmpty(error);
    }

    private static string CreateTaskXml(
        string command,
        bool taskEnabled = true,
        bool triggerEnabled = true,
        bool includeTaskEnabled = true,
        bool includeTriggerEnabled = true,
        bool includeLogonTrigger = true,
        string? runLevel = "LeastPrivilege",
        string? logonType = "InteractiveToken")
    {
        var triggerEnabledElement = includeTriggerEnabled
            ? $"<Enabled>{triggerEnabled.ToString().ToLowerInvariant()}</Enabled>"
            : string.Empty;
        var trigger = includeLogonTrigger
            ? $"<LogonTrigger>{triggerEnabledElement}</LogonTrigger>"
            : string.Empty;
        var taskEnabledElement = includeTaskEnabled
            ? $"<Enabled>{taskEnabled.ToString().ToLowerInvariant()}</Enabled>"
            : string.Empty;

        var principalElements = string.Concat(
            logonType == null ? string.Empty : $"<LogonType>{logonType}</LogonType>",
            runLevel == null ? string.Empty : $"<RunLevel>{runLevel}</RunLevel>");
        var principals = principalElements.Length == 0
            ? string.Empty
            : $"  <Principals><Principal id=\"Author\">{principalElements}</Principal></Principals>\n";

        var escapedCommand = System.Security.SecurityElement.Escape(command);
        return
            $"<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
            "<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
            $"  <Triggers>{trigger}</Triggers>\n" +
            $"  <Settings>{taskEnabledElement}</Settings>\n" +
            principals +
            "  <Actions Context=\"Author\">\n" +
            "    <Exec>\n" +
            $"      <Command>{escapedCommand}</Command>\n" +
            "    </Exec>\n" +
            "  </Actions>\n" +
            "</Task>";
    }
}
