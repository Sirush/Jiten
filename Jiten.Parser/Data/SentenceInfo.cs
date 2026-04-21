namespace Jiten.Parser;

public class SentenceInfo
{
    public string Text { get; set; } = string.Empty;
    public List<(WordInfo word, int position, int length)> Words { get; set; } = new();

    // Token boundaries from Sudachi before PreprocessSentences merges run.
    // The beam uses these as additional hint anchors so edges aligned with
    // Sudachi's raw segmentation can win against later-added compound merges.
    public List<(int position, int length)> RawSudachiBoundaries { get; set; } = new();

    public SentenceInfo(string text)
    {
        Text = text;
    }
}
