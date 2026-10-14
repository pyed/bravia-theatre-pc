using BraviaTheatre.UI.Services;

namespace BraviaTheatre.Tests;

public class AutoStartServiceTests
{
    [Theory]
    // Explorer runs an exe opened from inside a zip out of a folder like this one.
    [InlineData(@"C:\Users\someone\AppData\Local\Temp\Temp1_BraviaTheatrePC.zip\BraviaTheatrePC.exe", true)]
    [InlineData(@"C:\Users\someone\AppData\Local\Temp\BraviaTheatrePC.exe", true)]
    [InlineData(@"c:\users\SOMEONE\appdata\local\temp\x\BraviaTheatrePC.exe", true)]
    [InlineData(@"C:\Users\someone\Documents\bin\BraviaTheatrePC.exe", false)]
    [InlineData(@"C:\Users\someone\AppData\Local\Temporary\BraviaTheatrePC.exe", false)]
    public void RecognisesExecutablesRunningFromTheTempFolder(string executablePath, bool expected)
    {
        Assert.Equal(expected, AutoStartService.IsTemporaryLocation(executablePath, @"C:\Users\someone\AppData\Local\Temp\"));
        Assert.Equal(expected, AutoStartService.IsTemporaryLocation(executablePath, @"C:\Users\someone\AppData\Local\Temp"));
    }
}
