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

    // Moving the executable left the old Run entry behind; Settings then showed the box off and
    // saving deleted an entry that another, still existing copy relied on (HANDOFF P2-21).
    [Theory]
    [InlineData(null, false, RunEntryState.None)]
    [InlineData("", false, RunEntryState.None)]
    [InlineData("\"C:\\Apps\\BraviaTheatrePC.exe\"", true, RunEntryState.ThisCopy)]
    [InlineData("c:\\apps\\braviatheatrepc.exe", true, RunEntryState.ThisCopy)]
    [InlineData("\"C:\\Old\\BraviaTheatrePC.exe\"", true, RunEntryState.OtherCopy)]
    [InlineData("\"C:\\Old\\BraviaTheatrePC.exe\"", false, RunEntryState.MissingCopy)]
    public void TheRunEntryIsClassifiedAgainstTheRunningCopy(string? command, bool registeredFileExists, object expected)
    {
        var (state, _) = AutoStartService.Classify(command, @"C:\Apps\BraviaTheatrePC.exe", _ => registeredFileExists);

        Assert.Equal((RunEntryState)expected, state);
    }
}
