// Adapted from the monorepo: MapleKiosk/Infrastructure/VoiceAgent/SPC.Infrastructure.VoiceAgent/Twilio/TwilioTwiml.cs
// Only the three answers the website's line gives; the salon's transfer, dial and recording verbs are left out.

using System.Security;
using System.Text;

namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>The answers the phone webhook can give, as TwiML. String building: the vocabulary is tiny.</summary>
internal static class TwilioTwiml
{
    public const string ContentType = "text/xml; charset=utf-8";

    private const string Prologue = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>";

    /// <summary>
    /// Hand the call to the media socket. <c>&lt;Connect&gt;</c> is the bidirectional form: the call stays
    /// parked on the stream until the socket closes, and Twilio hangs up when it does.
    /// </summary>
    public static string ConnectStream(string streamUrl) =>
        Prologue
        + "<Response><Connect><Stream url=\"" + SecurityElement.Escape(streamUrl) + "\" /></Connect></Response>";

    /// <summary>Say each line in its own language, then hang up — for when nobody can take the call.</summary>
    public static string SayAndHangUp(IEnumerable<(string Text, string? LanguageTag)> lines)
    {
        var body = new StringBuilder();
        foreach (var (text, languageTag) in lines)
        {
            body.Append("<Say");
            if (!string.IsNullOrWhiteSpace(languageTag))
                body.Append(" language=\"").Append(SecurityElement.Escape(languageTag.Trim())).Append('"');
            body.Append('>').Append(SecurityElement.Escape(text)).Append("</Say>");
        }
        return Prologue + "<Response>" + body + "<Hangup/></Response>";
    }

    public static string Empty => Prologue + "<Response/>";
}
