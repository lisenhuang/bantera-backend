using BanteraApi.Practice;
using Xunit;

namespace BanteraApi.Tests;

public class PracticeClockExpressionTests
{
    [Theory]
    [InlineData("fr", "à 8 heures", "à 8:00")]
    [InlineData("fr", "à 8 heures 30", "à 8:30")]
    [InlineData("fr", "à 8 h 30", "à 8:30")]
    [InlineData("de", "um 8 uhr", "um 8:00")]
    [InlineData("de", "um 8 uhr 30", "um 8:30")]
    [InlineData("es", "a las 8", "a las 8:00")]
    [InlineData("it", "alle 8", "alle 8:00")]
    [InlineData("pt", "às 8 horas", "às 8:00")]
    [InlineData("nl", "om 8 uur", "om 8:00")]
    [InlineData("sv", "klockan 8", "8:00")]
    [InlineData("da", "klokken 8", "8:00")]
    [InlineData("nb", "klokka 8", "8:00")]
    [InlineData("fi", "kello 8", "8:00")]
    [InlineData("ru", "в 8 часов", "в 8:00")]
    [InlineData("uk", "о 8 годині", "о 8:00")]
    [InlineData("pl", "o godzinie 8", "o godzinie 8:00")]
    [InlineData("cs", "v 8 hodin", "v 8:00")]
    [InlineData("sk", "o 8 hodín", "o 8:00")]
    [InlineData("hr", "u 8 sati", "u 8:00")]
    [InlineData("ro", "la ora 8", "la ora 8:00")]
    [InlineData("hu", "8 órakor", "8:00-kor")]
    [InlineData("el", "στις 8", "στις 8:00")]
    [InlineData("tr", "saat 8", "8:00")]
    [InlineData("id", "pukul 8", "8:00")]
    [InlineData("ms", "pukul 8", "8:00")]
    [InlineData("vi", "lúc 8 giờ", "lúc 8:00")]
    [InlineData("hi", "8 बजे", "8:00")]
    [InlineData("ar", "الساعة 8", "8:00")]
    [InlineData("he", "בשעה 8", "8:00")]
    [InlineData("ca", "a les 8", "a les 8:00")]
    [InlineData("th", "8 นาฬิกา", "8:00")]
    public void ExplicitClockPhraseRetainsHourAndMinute(string language, string text, string expected)
        => Assert.Equal(expected, PracticeClockExpressions.Normalize(text, language));

    [Theory]
    [InlineData("fr", "à 8 heures et demie du matin", "à 8:30 am")]
    [InlineData("fr", "à 8 heures du soir", "à 8:00 pm")]
    [InlineData("es", "a las 8 y cuarto de la tarde", "a las 8:15 pm")]
    [InlineData("it", "alle 8 e mezza di sera", "alle 8:30 pm")]
    [InlineData("pt", "às 8 e meia da manhã", "às 8:30 am")]
    [InlineData("de", "um 8 uhr morgens", "um 8:00 am")]
    [InlineData("ru", "в 8 часов вечера", "в 8:00 pm")]
    [InlineData("ar", "الساعة 8 صباحًا", "8:00 am")]
    [InlineData("he", "בשעה 8 בערב", "8:00 pm")]
    public void ExplicitClockPeriodsRemainMeaningful(string language, string text, string expected)
        => Assert.Equal(expected, PracticeClockExpressions.Normalize(text, language));

    [Theory]
    [InlineData("fr", "8 heures de travail")]
    [InlineData("nl", "8 uur werken")]
    [InlineData("ru", "8 часов работы")]
    [InlineData("es", "a las 8 personas")]
    [InlineData("it", "alle 8 persone")]
    [InlineData("pt", "às 8 pessoas")]
    [InlineData("ca", "a les 8 persones")]
    [InlineData("th", "8 โมง")]
    [InlineData("fr", "à 80 heures")]
    [InlineData("de", "um 8 uhr 90")]
    [InlineData("fr", "à 8:30")]
    [InlineData("es", "a las 8:30:15")]
    [InlineData("de", "um 8:300")]
    [InlineData("fr", "à 8,5 heures")]
    public void DurationsInvalidTimesAndAlreadyFormattedClocksAreNotPartiallyRewritten(string language, string text)
        => Assert.Equal(text, PracticeClockExpressions.Normalize(text, language));

    [Theory]
    [InlineData("fr-FR", "à 8:00", "à huit heures")]
    [InlineData("de-DE", "um 8:00", "um acht uhr")]
    [InlineData("es-ES", "a las 8:00", "a las ocho")]
    [InlineData("it-IT", "alle 8:00", "alle otto")]
    [InlineData("pt-PT", "às 8:00", "às oito horas")]
    [InlineData("nl-NL", "om 8:00", "om acht uur")]
    [InlineData("sv-SE", "8:00", "klockan åtta")]
    [InlineData("da-DK", "8:00", "klokken otte")]
    [InlineData("nb-NO", "8:00", "klokka åtte")]
    [InlineData("fi-FI", "8:00", "kello kahdeksan")]
    public void MatcherUsesClockAliasesAfterSpellout(string locale, string transcript, string choice)
    {
        Assert.Equal(PracticeAnswerMatcher.Normalise(choice, locale), PracticeAnswerMatcher.Normalise(transcript, locale));
        Assert.Equal(0, PracticeAnswerMatcher.Match(transcript, [choice, "Tomorrow."], locale));
    }

    [Theory]
    [InlineData("fr-FR", "à 8:30", "à huit heures")]
    [InlineData("de-DE", "um 9:00", "um acht uhr")]
    [InlineData("fr-FR", "à 8:00 pm", "à huit heures du matin")]
    public void MatchingDoesNotLoseDifferentMinutesHoursOrMeridiem(string locale, string transcript, string choice)
        => Assert.Null(PracticeAnswerMatcher.Match(transcript, [choice, "Tomorrow."], locale));
}
