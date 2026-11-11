using System.Globalization;

namespace MapleKiosk.Web.Assistant;

/// <summary>
/// MapleKiosk's local time (Quebec, Eastern), told to the assistant on every channel so "tomorrow", "next
/// Tuesday" or "this afternoon" become a real date on a demo booking, a message or an order.
/// </summary>
public static class LocalClock
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");

    /// <summary>MapleKiosk's hours: every day, Monday to Sunday, 8:00 to 18:00 Quebec time.</summary>
    public const int OpensAt = 8, ClosesAt = 18;

    public static DateTimeOffset Now(DateTimeOffset? utc = null) =>
        TimeZoneInfo.ConvertTime(utc ?? DateTimeOffset.UtcNow, Eastern);

    /// <summary>Whether the team is working now (8:00 to 18:00, any day).</summary>
    public static bool IsOpen(DateTimeOffset? utc = null) => Now(utc).Hour is >= OpensAt and < ClosesAt;

    /// <summary>"Wednesday, November 11, 2026, 3:42 PM (Eastern time, Quebec)" plus how to use it.</summary>
    public static string Note(DateTimeOffset? utc = null)
    {
        var now = Now(utc);
        var en = CultureInfo.GetCultureInfo("en-US");
        return $"Right now it is {now.ToString("dddd, MMMM d, yyyy, h:mm tt", en)} in Quebec (Eastern time; tomorrow is " +
               $"{now.AddDays(1).ToString("dddd, MMMM d", en)}). When the guest gives a relative day or time (\"tomorrow\", " +
               "\"next Tuesday\", \"this afternoon\"), work out the actual date and use it: confirm it with them in words " +
               "(\"Thursday, November 12, in the afternoon?\"), and put the full date — weekday, month, day — in any tool call, " +
               "never just \"tomorrow\". " +
               "MapleKiosk is open every day, Monday to Sunday, 8 AM to 6 PM (Quebec time): a demo or a callback is " +
               "set within those hours. " +
               (IsOpen(utc) ? "The team is working right now." : "The team is off right now; they are back at 8 AM.");
    }
}
