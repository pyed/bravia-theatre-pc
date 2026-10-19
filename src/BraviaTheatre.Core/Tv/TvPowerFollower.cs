using System;
using System.Threading;
using System.Threading.Tasks;

namespace BraviaTheatre.Core.Tv;

/// <summary>
/// Turns the soundbar on when the TV turns on. Samsung sets switch a soundbar off over HDMI-CEC
/// reliably but often fail to switch it back on: at boot the TV looks for an eARC device before
/// the soundbar answers, falls back to its own speakers, and never sends the power-on.
/// </summary>
/// <remarks>
/// Only a confirmed off-to-on change of the TV acts. Starting while the TV is already on does
/// nothing, and turning the soundbar off while the TV stays on is left alone, so the user always
/// wins. Two matching readings in a row confirm a change, so one odd answer cannot wake the
/// soundbar in the night.
/// </remarks>
public sealed class TvPowerFollower
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan WakeRetryInterval = TimeSpan.FromSeconds(1);
    internal const int ReadingsToConfirm = 2;

    // The soundbar's control service can still be coming back when the TV reports on (for
    // example after the PC resumes); keep trying for this many retries before giving up.
    internal const int WakeAttempts = 30;

    private readonly Func<CancellationToken, Task<TvPowerReading>> _readTv;
    private readonly Func<bool?> _soundbarPower;
    private readonly Func<Task<bool>> _turnSoundbarOn;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<string, DiagnosticLevel>? _log;

    /// <param name="readTv">One look at the TV.</param>
    /// <param name="soundbarPower">The soundbar's power, or null while it is not connected.</param>
    /// <param name="turnSoundbarOn">Asks the soundbar to turn on; true when the request was sent.</param>
    /// <param name="delay">Waits between readings; injectable for tests.</param>
    /// <param name="log">Diagnostics; never given addresses.</param>
    public TvPowerFollower(
        Func<CancellationToken, Task<TvPowerReading>> readTv,
        Func<bool?> soundbarPower,
        Func<Task<bool>> turnSoundbarOn,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<string, DiagnosticLevel>? log = null)
    {
        _readTv = readTv ?? throw new ArgumentNullException(nameof(readTv));
        _soundbarPower = soundbarPower ?? throw new ArgumentNullException(nameof(soundbarPower));
        _turnSoundbarOn = turnSoundbarOn ?? throw new ArgumentNullException(nameof(turnSoundbarOn));
        _delay = delay ?? (static (span, ct) => Task.Delay(span, ct));
        _log = log;
    }

    /// <summary>Polls until cancelled. Cancellation ends it with <see cref="OperationCanceledException"/>.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        TvPowerReading? confirmed = null;
        TvPowerReading? last = null;
        var streak = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var reading = await ReadTvAsync(ct).ConfigureAwait(false);
            streak = reading == last ? streak + 1 : 1;
            last = reading;

            if (streak >= ReadingsToConfirm && reading != confirmed)
            {
                var previous = confirmed;
                confirmed = reading;
                _log?.Invoke(
                    reading == TvPowerReading.On ? "TV follow: TV is on." : "TV follow: TV is off.",
                    DiagnosticLevel.Info);

                if (reading == TvPowerReading.On && previous == TvPowerReading.NotOn)
                    await WakeSoundbarAsync(ct).ConfigureAwait(false);
            }

            await _delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    private async Task<TvPowerReading> ReadTvAsync(CancellationToken ct)
    {
        try
        {
            return await _readTv(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log?.Invoke($"TV follow: reading the TV failed ({ex.GetType().Name}).", DiagnosticLevel.Verbose);
            return TvPowerReading.NotOn;
        }
    }

    private async Task WakeSoundbarAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < WakeAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            switch (_soundbarPower())
            {
                case true:
                    _log?.Invoke("TV follow: the soundbar is already on.", DiagnosticLevel.Info);
                    return;

                case false when await _turnSoundbarOn().ConfigureAwait(false):
                    _log?.Invoke("TV follow: the TV turned on, so the soundbar was turned on.", DiagnosticLevel.Critical);
                    return;
            }

            await _delay(WakeRetryInterval, ct).ConfigureAwait(false);
        }

        _log?.Invoke(
            "TV follow: the TV turned on, but the soundbar could not be reached to turn it on.",
            DiagnosticLevel.Critical);
    }
}
