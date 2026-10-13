using System.Runtime.CompilerServices;
using BraviaTheatre.UI;
using BraviaTheatre.UI.Services;

namespace BraviaTheatre.Tests;

public class ErrorReportingTests
{
    [Fact]
    public void OnlyOneErrorDialogIsOpenAtATime()
    {
        var gate = new ErrorDialogGate();
        Assert.True(gate.TryOpen(FailToLoadLibrary()));

        // A modal dialog pumps messages, so the next failure arrives while it is still open.
        Assert.False(gate.TryOpen(FailWithInvalidOperation()));

        gate.Close();
        Assert.True(gate.TryOpen(FailWithInvalidOperation()));
    }

    [Fact]
    public void RepeatedFailureShowsAgainOnlyAfterTheRepeatInterval()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 13, 16, 55, 55, TimeSpan.Zero));
        var gate = new ErrorDialogGate(clock);
        Assert.True(gate.TryOpen(FailToLoadLibrary()));
        gate.Close();

        clock.Advance(ErrorDialogGate.RepeatInterval - TimeSpan.FromSeconds(1));
        Assert.False(gate.TryOpen(FailToLoadLibrary()));
        Assert.True(gate.TryOpen(FailWithInvalidOperation()));
        gate.Close();

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(gate.TryOpen(FailToLoadLibrary()));
    }

    [Fact]
    public void LogEntryShowsWhereAGenericFailureCameFrom()
    {
        var entry = App.FormatExceptionForLog(FailToLoadLibrary());

        // "Dll was not found." alone names neither the library nor the caller.
        Assert.StartsWith("DllNotFoundException: Dll was not found. | at ", entry, StringComparison.Ordinal);
        Assert.Contains($"{nameof(ErrorReportingTests)}.{nameof(FailToLoadLibrary)}()", entry, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', entry);
    }

    [Fact]
    public void LogEntryUsesTheInnermostStackTrace()
    {
        var entry = App.FormatExceptionForLog(new InvalidOperationException("Outer.", FailToLoadLibrary()));

        Assert.StartsWith("InvalidOperationException: Outer. -> DllNotFoundException: Dll was not found. | at ", entry, StringComparison.Ordinal);
        Assert.Contains(nameof(FailToLoadLibrary), entry, StringComparison.Ordinal);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception FailToLoadLibrary()
    {
        try
        {
            throw new DllNotFoundException();
        }
        catch (DllNotFoundException exception)
        {
            return exception;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception FailWithInvalidOperation()
    {
        try
        {
            throw new InvalidOperationException();
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
