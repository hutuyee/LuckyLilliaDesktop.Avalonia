using LuckyLilliaDesktop.Utils;
using System.Linq;
using System.Text;
using Xunit;

namespace LuckyLilliaDesktop.Tests;

public class ScheduledTaskClientTests
{
    [Fact]
    public void DecodeProcessOutput_Utf8WithoutBom_PreservesText()
    {
        const string expected = "任务创建失败：拒绝访问";
        var bytes = Encoding.UTF8.GetBytes(expected);

        var actual = ScheduledTaskClient.DecodeProcessOutput(bytes);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void DecodeProcessOutput_Utf8WithBom_PreservesText()
    {
        const string expected = "<?xml version=\"1.0\"?><Task />";
        var payload = Encoding.UTF8.GetBytes(expected);
        var bytes = Encoding.UTF8.GetPreamble().Concat(payload).ToArray();

        var actual = ScheduledTaskClient.DecodeProcessOutput(bytes);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void DecodeProcessOutput_Utf16LittleEndianWithBom_PreservesXml()
    {
        const string expected = "<?xml version=\"1.0\"?><Task>测试</Task>";
        var payload = Encoding.Unicode.GetBytes(expected);
        var bytes = Encoding.Unicode.GetPreamble().Concat(payload).ToArray();

        var actual = ScheduledTaskClient.DecodeProcessOutput(bytes);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void DecodeProcessOutput_Utf16LittleEndianWithoutBom_PreservesXml()
    {
        const string expected = "<?xml version=\"1.0\"?><Task>测试</Task>";
        var bytes = Encoding.Unicode.GetBytes(expected);

        var actual = ScheduledTaskClient.DecodeProcessOutput(bytes);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void DecodeProcessOutput_Utf16BigEndianWithoutBom_PreservesXml()
    {
        const string expected = "<?xml version=\"1.0\"?><Task>测试</Task>";
        var bytes = Encoding.BigEndianUnicode.GetBytes(expected);

        var actual = ScheduledTaskClient.DecodeProcessOutput(bytes);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void DecodeProcessOutput_EmptyBytes_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, ScheduledTaskClient.DecodeProcessOutput([]));
    }
}
