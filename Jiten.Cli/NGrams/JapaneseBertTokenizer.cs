using Microsoft.Extensions.Logging;

namespace Jiten.Cli.NGrams;

public class JapaneseBertTokenizer
{
    private readonly Dictionary<string, int> _vocab;
    private readonly Dictionary<int, string> _inverseVocab;

    private const string UnknownToken = "[UNK]";
    private const string PadToken = "[PAD]";
    private const string ClsToken = "[CLS]";
    private const string SepToken = "[SEP]";
    private const string MaskToken = "[MASK]";

    private readonly int _unkId;
    private readonly int _padId;
    private readonly int _clsId;
    private readonly int _sepId;
    private readonly int _maskId;

    public JapaneseBertTokenizer(string vocabPath)
    {
        _vocab = new Dictionary<string, int>();
        _inverseVocab = new Dictionary<int, string>();

        if (!string.IsNullOrEmpty(vocabPath) && File.Exists(vocabPath))
        {
            LoadVocabulary(vocabPath);
        }
        else
        {
            Console.WriteLine($"Vocabulary file not found at {vocabPath}");
            // Initialize with minimal vocab
            InitializeMinimalVocab();
        }

        _unkId = _vocab.GetValueOrDefault(UnknownToken, 0);
        _padId = _vocab.GetValueOrDefault(PadToken, 1);
        _clsId = _vocab.GetValueOrDefault(ClsToken, 2);
        _sepId = _vocab.GetValueOrDefault(SepToken, 3);
        _maskId = _vocab.GetValueOrDefault(MaskToken, 4);
    }

    private void LoadVocabulary(string vocabPath)
    {
        var lines = File.ReadAllLines(vocabPath);

        for (int i = 0; i < lines.Length; i++)
        {
            var token = lines[i].Trim();
            if (!string.IsNullOrEmpty(token))
            {
                _vocab[token] = i;
                _inverseVocab[i] = token;
            }
        }

        Console.WriteLine($"Loaded {_vocab.Count} tokens from vocabulary");
    }

    private void InitializeMinimalVocab()
    {
        // Minimal vocab for testing
        var specialTokens = new[] { UnknownToken, PadToken, ClsToken, SepToken, MaskToken };

        for (int i = 0; i < specialTokens.Length; i++)
        {
            _vocab[specialTokens[i]] = i;
            _inverseVocab[i] = specialTokens[i];
        }
    }

    /// <summary>
    /// Tokenize Japanese text
    /// </summary>
    public List<string> Tokenize(string text)
    {
        var tokens = new List<string> { ClsToken };

        // Simple character-level tokenization for Japanese
        // In production, you'd use MeCab or Sudachi integration
        foreach (var character in text)
        {
            var charStr = character.ToString();

            // Try to find in vocab
            if (_vocab.ContainsKey(charStr))
            {
                tokens.Add(charStr);
            }
            else
            {
                // Use WordPiece-style subword if available
                var subwords = WordPieceTokenize(charStr);
                tokens.AddRange(subwords);
            }
        }

        tokens.Add(SepToken);
        return tokens;
    }

    /// <summary>
    /// Convert tokens to IDs
    /// </summary>
    public int[] ConvertToIds(List<string> tokens)
    {
        return tokens.Select(t => _vocab.GetValueOrDefault(t, _unkId)).ToArray();
    }

    /// <summary>
    /// Convert IDs back to tokens
    /// </summary>
    public List<string> ConvertIdsToTokens(int[] ids)
    {
        return ids.Select(id => _inverseVocab.GetValueOrDefault(id, UnknownToken)).ToList();
    }

    /// <summary>
    /// WordPiece tokenization for unknown characters
    /// </summary>
    private List<string> WordPieceTokenize(string word)
    {
        var tokens = new List<string>();
        int start = 0;

        while (start < word.Length)
        {
            int end = word.Length;
            string? subword = null;

            // Greedy longest-match-first
            while (start < end)
            {
                string substr = word.Substring(start, end - start);
                if (start > 0)
                {
                    substr = "##" + substr; // Continuation marker
                }

                if (_vocab.ContainsKey(substr))
                {
                    subword = substr;
                    break;
                }

                end--;
            }

            if (subword == null)
            {
                tokens.Add(UnknownToken);
                break;
            }

            tokens.Add(subword);
            start = end;
        }

        return tokens;
    }
}