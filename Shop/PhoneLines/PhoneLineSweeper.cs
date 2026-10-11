namespace MapleKiosk.Web.Shop.PhoneLines;

/// <summary>
/// Every few minutes, holds each line to its monthly minute cap (<see cref="PhoneLineService.EnforceCapsAsync"/>) and
/// hands back to Twilio the numbers of lines whose subscription ended more than the grace period ago
/// (<see cref="PhoneLineService.ReleaseDueAsync"/>). The rules are the service's; this is only the clock.
/// </summary>
public sealed class PhoneLineSweeper : BackgroundService
{
    private static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(15);

    private readonly PhoneLineService _phoneLines;
    private readonly ILogger<PhoneLineSweeper> _logger;

    public PhoneLineSweeper(PhoneLineService phoneLines, ILogger<PhoneLineSweeper> logger)
    {
        _phoneLines = phoneLines;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(FirstRun, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Every);
        do
        {
            try
            {
                await _phoneLines.EnforceCapsAsync(DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
                await _phoneLines.ReleaseDueAsync(DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Phone line sweep failed; trying again next round.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
