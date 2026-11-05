using MapleKiosk.Web.Assistant.Voice;
using Xunit;

namespace MapleKiosk.Web.Tests;

/// <summary>The website's phone line: what Twilio is told, who the caller is, and which requests are Twilio's.</summary>
public sealed class PhoneHookupTests
{
    [Fact]
    public void Connect_parks_the_call_on_the_stream_with_the_url_escaped()
    {
        var xml = TwilioTwiml.ConnectStream("wss://www.maplekiosk.ca/twilio/voice/stream/abc?x=1&y=2");
        Assert.Contains("<Connect><Stream url=\"wss://www.maplekiosk.ca/twilio/voice/stream/abc?x=1&amp;y=2\" /></Connect>", xml);
    }

    [Fact]
    public void Unavailable_says_each_line_in_its_language_then_hangs_up()
    {
        var xml = TwilioTwiml.SayAndHangUp([("Sorry & bye", "en-US"), ("Désolé", "fr-CA")]);
        Assert.EndsWith("<Response><Say language=\"en-US\">Sorry &amp; bye</Say><Say language=\"fr-CA\">Désolé</Say><Hangup/></Response>", xml);
    }

    [Theory]
    [InlineData("+16045550123", "+17785550000", "+16045550123")]
    [InlineData("anonymous", "+17785550000", null)]
    [InlineData("", "+17785550000", null)]
    // A forward that dropped the caller id presents the line's own number: that says nothing about who is calling.
    [InlineData("+17785550000", "+17785550000", null)]
    public void Caller_number_is_e164_or_nothing(string from, string to, string? expected) =>
        Assert.Equal(expected, WebsitePhoneEndpoints.GuestNumber(from, to));

    [Fact]
    public void The_webhooks_given_for_twilio_are_the_public_sites() =>
        Assert.Equal("https://www.maplekiosk.ca/twilio/voice/incoming", PhoneHookup.IncomingUrl);

    [Fact]
    public void Numbers_compare_by_digits() =>
        Assert.Equal(PhoneHookup.Digits("+1 (604) 555-0123"), PhoneHookup.Digits("+16045550123"));

    [Fact]
    public void A_signature_from_the_token_verifies_and_a_tampered_form_does_not()
    {
        const string token = "test-token", url = "https://www.maplekiosk.ca/twilio/voice/incoming";
        var form = new[] { new KeyValuePair<string, string>("CallSid", "CA1"), new("From", "+16045550123") };
        var signature = TwilioSignature.Compute(token, url, form);

        Assert.True(TwilioSignature.IsValid(token, url, form, signature));
        Assert.False(TwilioSignature.IsValid(token, url, [new("CallSid", "CA2"), new("From", "+16045550123")], signature));
        Assert.False(TwilioSignature.IsValid("other-token", url, form, signature));
    }
}
