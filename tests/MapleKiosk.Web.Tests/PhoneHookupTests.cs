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

/// <summary>The Quebec greeting and the SMS line on the same number.</summary>
public sealed class QuebecVoiceAndSmsTests
{
    [Fact]
    public void Phone_line_opens_bilingual_by_default() =>
        Assert.Equal(MapleKioskVoicePersona.Bilingual, PhoneHookup.DefaultLanguage);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Bilingual_greeting_is_bonjour_hi_french_first(bool phone)
    {
        var greeting = new MapleKioskVoicePersona(MapleKioskVoicePersona.Bilingual, phone)
            .GreetingFor(VoiceToolAudience.Customer, "MapleKiosk");
        Assert.StartsWith("Bonjour, hi!", greeting);
        Assert.Contains("À qui ai-je le plaisir de parler?", greeting);
        Assert.Contains("May I have your name?", greeting);
    }

    [Fact]
    public void Bilingual_prompt_follows_the_callers_language()
    {
        var note = MapleKioskVoicePersona.PhoneLanguageNote(MapleKioskVoicePersona.Bilingual);
        Assert.Contains("continue only in the language they speak", note);
        Assert.Contains("Vietnamese", note);
        Assert.Contains("Quebec", note);
    }

    [Fact]
    public void Phone_test_profile_carries_the_bilingual_note()
    {
        var profile = WebsitePhoneEndpoints.PhoneProfile(new MapleKiosk.Web.Assistant.VoiceSetup("k", "m", "v"), "qc");
        Assert.Equal(MapleKioskVoicePersona.PhoneLanguageNote("qc"), profile.PromptAddendum);
        Assert.Equal("m", profile.Model);
    }

    [Fact]
    public void Sms_webhook_is_the_public_sites() =>
        Assert.Equal("https://www.maplekiosk.ca/twilio/sms/incoming", WebsiteSmsEndpoints.IncomingUrl);

    [Fact]
    public void One_sms_conversation_per_sender_whatever_the_formatting()
    {
        var id = WebsiteSmsEndpoints.ConversationId("+15145550123");
        Assert.Equal(32, id.Length);
        Assert.Equal(id, MapleKiosk.Web.Assistant.ChatLog.IdFor(id));
        Assert.Equal(id, WebsiteSmsEndpoints.ConversationId("+1 (514) 555-0123"));
        Assert.NotEqual(id, WebsiteSmsEndpoints.ConversationId("+15145550124"));
    }

    [Fact]
    public void Sms_replies_are_plain_text()
    {
        var plain = WebsiteSmsEndpoints.Plain(
            "**Prix:** voir [la page des prix](/fr/coffee/pricing).\n* Un\n* Deux\n[[Café]]\n[[Salon]]");
        Assert.Equal("Prix: voir la page des prix: https://www.maplekiosk.ca/fr/coffee/pricing.\n- Un\n- Deux", plain);
    }
}
