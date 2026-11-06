namespace Jiten.Core.Data.JMDict;

public static class PitchAccentResolver
{
    /// <summary>The form's own accent; the word-level value stands in only for forms the per-form import has not reached.</summary>
    public static List<int>? For(JmDictWordForm? form, List<int>? wordAccents)
        => form?.PitchAccents ?? wordAccents;

    public static List<int>? For(JmDictWord word, JmDictWordForm? form)
        => For(form, word.PitchAccents);
}
