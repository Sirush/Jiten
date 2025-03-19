using System.Runtime.InteropServices;
using System.Text;
using Jiten.Core.Data;

namespace Jiten.Parser;

class SudachiInterop
{
    // Import the `run_cli_ffi` function from the Rust DLL
    [DllImport("resources/sudachi_lib.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr run_cli_ffi(string configPath, string filePath, string dictionaryPath, string outputPath);

    [DllImport("resources/sudachi_lib.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr process_text_ffi(string configPath, IntPtr inputText, string dictionaryPath, char mode, bool printAll,
                                                  bool wakati);


    // Import the `free_string` function to free memory allocated in Rust
    [DllImport("resources/sudachi_lib.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void free_string(IntPtr ptr);

    private static readonly object ProcessTextLock = new object();


    public static string RunCli(string configPath, string filePath, string dictionaryPath, string outputPath)
    {
        // Call the FFI function
        IntPtr resultPtr = run_cli_ffi(configPath, filePath, dictionaryPath, outputPath);

        // Convert the result to a C# string
        string result = Marshal.PtrToStringAnsi(resultPtr) ?? string.Empty;

        // Free the string allocated in Rust
        free_string(resultPtr);

        return result;
    }

    public static string ProcessText(string configPath, string inputText, string dictionaryPath, char mode = 'C', bool printAll = true,
                                     bool wakati = false)
    {
        lock (ProcessTextLock)
        {
            byte[] inputBytes = Encoding.UTF8.GetBytes(inputText + "\0");
            IntPtr inputTextPtr = Marshal.AllocHGlobal(inputBytes.Length);
            Marshal.Copy(inputBytes, 0, inputTextPtr, inputBytes.Length);

            IntPtr resultPtr = process_text_ffi(configPath, inputTextPtr, dictionaryPath, mode, printAll, wakati);
            string result = Marshal.PtrToStringUTF8(resultPtr) ?? string.Empty;

            free_string(resultPtr);

            Marshal.FreeHGlobal(inputTextPtr);

            return result;
        }
    }
}

public class Parser
{
    private static HashSet<(string, string, string, PartOfSpeech?)> SpecialCases3 =
    [
        ("な", "の", "で", PartOfSpeech.Expression),
        ("で", "は", "ない", PartOfSpeech.Expression),
        ("それ", "で", "も", PartOfSpeech.Conjunction),
        ("なく", "なっ", "た", PartOfSpeech.Verb)
    ];

    private static HashSet<(string, string, PartOfSpeech?)> SpecialCases2 =
    [
        ("じゃ", "ない", PartOfSpeech.Expression),
        ("に", "しろ", PartOfSpeech.Expression),
        ("だ", "けど", PartOfSpeech.Conjunction),
        ("だ", "が", PartOfSpeech.Conjunction),
        ("で", "さえ", PartOfSpeech.Expression),
        ("で", "すら", PartOfSpeech.Expression),
        ("と", "いう", PartOfSpeech.Expression),
        ("と", "か", PartOfSpeech.Conjunction),
        ("だ", "から", PartOfSpeech.Conjunction),
        ("これ", "まで", PartOfSpeech.Expression),
        ("それ", "も", PartOfSpeech.Conjunction),
        ("それ", "だけ", PartOfSpeech.Noun),
        ("くせ", "に", PartOfSpeech.Conjunction),
        ("の", "で", PartOfSpeech.Particle),
        ("誰", "も", PartOfSpeech.Expression),
        ("誰", "か", PartOfSpeech.Expression),
        ("すぐ", "に", PartOfSpeech.Adverb),
        ("なん", "か", PartOfSpeech.Particle),
        ("だっ", "た", PartOfSpeech.Expression),
        ("だっ", "たら", PartOfSpeech.Conjunction),
        ("よう", "に", PartOfSpeech.Expression),
        ("ん", "です", PartOfSpeech.Expression),
        ("ん", "だ", PartOfSpeech.Expression),
        ("です", "か", PartOfSpeech.Expression)
    ];

    private static readonly List<string> HonorificsSuffixes = ["さん", "ちゃん", "くん"];

    public async Task<List<WordInfo>> Parse(string text)
    {
        // Build dictionary  sudachi ubuild Y:\CODE\Jiten\Shared\resources\user_dic.xml -s F:\00_RawJap\sudachi.rs\resources\system_full.dic -o "Y:\CODE\Jiten\Shared\resources\user_dic.dic"

        // Preprocess the text to remove invalid characters
        PreprocessText(ref text);

        var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources", "sudachi.json");
        var dic = @"F:\00_RawJap\sudachi.rs\resources\system_full.dic";

        var output = SudachiInterop.ProcessText(configPath, text, dic).Split("\n");

        List<WordInfo> wordInfos = new();

        foreach (var line in output)
        {
            if (line == "EOS") continue;

            var wi = new WordInfo(line);
            if (!wi.IsInvalid)
                wordInfos.Add(wi);
        }

        wordInfos = ProcessSpecialCases(wordInfos);
        wordInfos = CombineConjunctiveParticle(wordInfos);
        wordInfos = CombinePrefixes(wordInfos);
        wordInfos = CombineAmounts(wordInfos);
        wordInfos = CombineTte(wordInfos);
        wordInfos = CombineAuxiliaryVerbStem(wordInfos);
        wordInfos = CombineAuxiliary(wordInfos);
        wordInfos = CombineVerbDependant(wordInfos);
        wordInfos = CombineAdverbialParticle(wordInfos);
        wordInfos = CombineSuffix(wordInfos);
        wordInfos = CombineParticles(wordInfos);

        wordInfos = CombineFinal(wordInfos);

        wordInfos = SeparateSuffixHonorifics(wordInfos);

        return wordInfos;
    }

    private void PreprocessText(ref string text)
    {
        text = text.Replace("<", " ");
        text = text.Replace(">", " ");
    }

    /// <summary>
    /// Handle special cases that could not be covered by the other rules
    /// </summary>
    /// <param name="wordInfos"></param>
    /// <returns></returns>
    private List<WordInfo> ProcessSpecialCases(List<WordInfo> wordInfos)
    {
        for (int i = 0; i < wordInfos.Count - 2; i++)
        {
            // surukudasai
            if (wordInfos[i].DictionaryForm == "する" && wordInfos[i + 1].Text == "て" && wordInfos[i + 2].DictionaryForm == "くださる")
            {
                wordInfos[i].Text = wordInfos[i].Text + wordInfos[i + 1].Text + wordInfos[i + 2].Text;
                wordInfos.RemoveAt(i + 1);
                wordInfos.RemoveAt(i + 1);
            }

            foreach (var sc in SpecialCases3)
            {
                if (wordInfos[i].Text == sc.Item1 && wordInfos[i + 1].Text == sc.Item2 && wordInfos[i + 2].Text == sc.Item3)
                {
                    wordInfos[i].Text = wordInfos[i].Text + wordInfos[i + 1].Text + wordInfos[i + 2].Text;
                    wordInfos.RemoveAt(i + 1);
                    wordInfos.RemoveAt(i + 1);

                    if (sc.Item4 != null)
                    {
                        wordInfos[i].PartOfSpeech = sc.Item4.Value;
                    }
                }
            }
        }

        for (int i = 0; i < wordInfos.Count - 1; i++)
        {
            foreach (var sc in SpecialCases2)
            {
                if (wordInfos[i].Text == sc.Item1 && wordInfos[i + 1].Text == sc.Item2)
                {
                    wordInfos[i].Text += wordInfos[i + 1].Text;
                    wordInfos.RemoveAt(i + 1);

                    if (sc.Item3 != null)
                    {
                        wordInfos[i].PartOfSpeech = sc.Item3.Value;
                    }
                }
            }
        }

        for (int i = 0; i < wordInfos.Count; i++)
        {
            // This word is (sometimes?) parsed as auxiliary for some reason
            if (wordInfos[i].Text == "でしょう")
            {
                wordInfos[i].PartOfSpeech = PartOfSpeech.Expression;
            }
        }

        return wordInfos;
    }

    private List<WordInfo> CombinePrefixes(List<WordInfo> wordInfos)
    {
        for (int i = 0; i < wordInfos.Count - 1; i++)
        {
            if (wordInfos[i].PartOfSpeech == PartOfSpeech.Prefix)
            {
                wordInfos[i + 1].Text = wordInfos[i].Text + wordInfos[i + 1].Text;
                wordInfos.RemoveAt(i);
                i--;
            }
        }

        return wordInfos;
    }

    private List<WordInfo> CombineAmounts(List<WordInfo> wordInfos)
    {
        for (int i = 0; i < wordInfos.Count - 1; i++)
        {
            if (wordInfos[i].HasPartOfSpeechSection(PartOfSpeechSection.Amount) ||
                wordInfos[i].HasPartOfSpeechSection(PartOfSpeechSection.Numeral))
            {
                wordInfos[i + 1].Text = wordInfos[i].Text + wordInfos[i + 1].Text;
                wordInfos[i + 1].PartOfSpeech = PartOfSpeech.Noun;
                wordInfos.RemoveAt(i);
                i--;
            }
        }

        return wordInfos;
    }

    private List<WordInfo> CombineTte(List<WordInfo> wordInfos)
    {
        for (int i = 0; i < wordInfos.Count - 1; i++)
        {
            if (wordInfos[i].Text.EndsWith("っ") && wordInfos[i + 1].Text.StartsWith("て"))
            {
                wordInfos[i].Text += wordInfos[i + 1].Text;
                wordInfos.RemoveAt(i + 1);
                i--;
            }
        }

        return wordInfos;
    }

    private List<WordInfo> CombineVerbDependant(List<WordInfo> wordInfos)
    {
        // Dependants
        for (int i = 1; i < wordInfos.Count; i++)
        {
            if (wordInfos[i].HasPartOfSpeechSection(PartOfSpeechSection.Dependant) &&
                wordInfos[i - 1].PartOfSpeech == PartOfSpeech.Verb)
            {
                wordInfos[i - 1].Text += wordInfos[i].Text;
                wordInfos.RemoveAt(i);
                i--;
            }
        }

        // Possible dependants, might need special rules?
        for (int i = 1; i < wordInfos.Count; i++)
        {
            if (wordInfos[i].HasPartOfSpeechSection(PartOfSpeechSection.PossibleDependant) &&
                wordInfos[i - 1].PartOfSpeech == PartOfSpeech.Verb &&
                wordInfos[i].DictionaryForm != "ござる" &&
                wordInfos[i].DictionaryForm != "かける" &&
                wordInfos[i].DictionaryForm != "あげる" &&
                wordInfos[i].DictionaryForm != "くれる" &&
                wordInfos[i].DictionaryForm != "終わる" &&
                wordInfos[i].DictionaryForm != "欲しい" &&
                wordInfos[i].DictionaryForm != "始める" &&
                wordInfos[i].DictionaryForm != "下さる" &&
                wordInfos[i].DictionaryForm != "貰う" &&
                wordInfos[i].DictionaryForm != "貰える" &&
                wordInfos[i].DictionaryForm != "なる" &&
                wordInfos[i].DictionaryForm != "いい"
               )
            {
                wordInfos[i - 1].Text += wordInfos[i].Text;
                wordInfos.RemoveAt(i);
                i--;
            }
        }

        // 注意してください
        for (int i = 0; i < wordInfos.Count - 1; i++)
        {
            if (wordInfos[i].HasPartOfSpeechSection(PartOfSpeechSection.PossibleSuru) &&
                wordInfos[i + 1].DictionaryForm == "する" && wordInfos[i + 1].Text != "する")
            {
                wordInfos[i].Text += wordInfos[i + 1].Text;
                wordInfos[i].PartOfSpeech = PartOfSpeech.Verb;
                wordInfos.RemoveAt(i + 1);
            }
        }

        // ている
        for (int i = 0; i < wordInfos.Count - 2; i++)
        {
            if (wordInfos[i].PartOfSpeech == PartOfSpeech.Verb && wordInfos[i + 1].DictionaryForm == "て" &&
                wordInfos[i + 2].DictionaryForm == "いる")
            {
                wordInfos[i].Text += wordInfos[i + 1].Text;
                wordInfos[i].Text += wordInfos[i + 2].Text;
                wordInfos.RemoveAt(i + 2);
                wordInfos.RemoveAt(i + 1);
            }
        }

        return wordInfos;
    }

    private List<WordInfo> CombineAdverbialParticle(List<WordInfo> wordInfos)
    {
        // Dependants
        for (int i = 1; i < wordInfos.Count; i++)
        {
            // i.e　だり, たり
            if (wordInfos[i].HasPartOfSpeechSection(PartOfSpeechSection.AdverbialParticle) &&
                (wordInfos[i].DictionaryForm == "だり" || wordInfos[i].DictionaryForm == "たり") &&
                wordInfos[i - 1].PartOfSpeech == PartOfSpeech.Verb)
            {
                wordInfos[i - 1].Text += wordInfos[i].Text;
                wordInfos.RemoveAt(i);
                i--;
            }
        }

        return wordInfos;
    }

    private List<WordInfo> CombineConjunctiveParticle(List<WordInfo> wordInfos)
    {
        for (int i = 1; i < wordInfos.Count; i++)
        {
            if (wordInfos[i].HasPartOfSpeechSection(PartOfSpeechSection.ConjunctionParticle) &&
                wordInfos[i].Text is "て" or "で" or "ながら" or "ちゃ" or "ば" &&
                wordInfos[i - 1].PartOfSpeech == PartOfSpeech.Verb)
            {
                wordInfos[i - 1].Text += wordInfos[i].Text;
                wordInfos.RemoveAt(i);
                i--;
            }
        }

        return wordInfos;
    }

    private List<WordInfo> CombineAuxiliary(List<WordInfo> wordInfos)
    {
        for (int i = 1; i < wordInfos.Count; i++)
        {
            if (wordInfos[i].PartOfSpeech == PartOfSpeech.Auxiliary
                && (wordInfos[i - 1].PartOfSpeech == PartOfSpeech.Verb ||
                    wordInfos[i - 1].PartOfSpeech == PartOfSpeech.IAdjective)
                && wordInfos[i].Text != "な"
                && wordInfos[i].DictionaryForm != "です"
                && wordInfos[i].DictionaryForm != "らしい"
                && wordInfos[i].Text != "なら"
                && wordInfos[i].DictionaryForm != "だ"
               )
            {
                wordInfos[i - 1].Text += wordInfos[i].Text;
                wordInfos.RemoveAt(i);
                i--;
            }

            if (wordInfos[i].PartOfSpeech == PartOfSpeech.Auxiliary &&
                (wordInfos[i - 1].HasPartOfSpeechSection(PartOfSpeechSection.PossibleNaAdjective) ||
                 wordInfos[i - 1].PartOfSpeech == PartOfSpeech.NaAdjective)
                && wordInfos[i].Text == "な")
            {
                wordInfos[i - 1].Text += wordInfos[i].Text;
                wordInfos[i - 1].PartOfSpeech = PartOfSpeech.NaAdjective;
                wordInfos.RemoveAt(i);
                i--;
            }
        }

        return wordInfos;
    }

    private List<WordInfo> CombineAuxiliaryVerbStem(List<WordInfo> wordInfos)
    {
        for (int i = 1; i < wordInfos.Count; i++)
        {
            if ( /*wordInfos[i].AnyPartOfSpeechSection(PartOfSpeechSection.Suffix) &&*/
                wordInfos[i].HasPartOfSpeechSection(PartOfSpeechSection.AuxiliaryVerbStem) &&
                wordInfos[i].Text != "ように" &&
                (wordInfos[i - 1].PartOfSpeech == PartOfSpeech.Verb || wordInfos[i - 1].PartOfSpeech == PartOfSpeech.IAdjective))
            {
                wordInfos[i - 1].Text += wordInfos[i].Text;
                wordInfos.RemoveAt(i);
                i--;
            }
        }

        return wordInfos;
    }

    private List<WordInfo> CombineSuffix(List<WordInfo> wordInfos)
    {
        for (int i = 1; i < wordInfos.Count; i++)
        {
            if ((wordInfos[i].PartOfSpeech == PartOfSpeech.Suffix || wordInfos[i].HasPartOfSpeechSection(PartOfSpeechSection.Suffix))
                && wordInfos[i].DictionaryForm != "っぽい" && wordInfos[i].DictionaryForm != "にくい" &&
                (wordInfos[i].DictionaryForm != "たち" || wordInfos[i - 1].PartOfSpeech == PartOfSpeech.Pronoun))
            {
                wordInfos[i - 1].Text += wordInfos[i].Text;
                wordInfos.RemoveAt(i);
                i--;
            }
        }

        return wordInfos;
    }

    private List<WordInfo> CombineParticles(List<WordInfo> wordInfos)
    {
        for (int i = 0; i < wordInfos.Count - 1; i++)
        {
            // には
            if (wordInfos[i].Text == "に" && wordInfos[i + 1].Text == "は")
            {
                wordInfos[i].Text = "には";
                wordInfos.RemoveAt(i + 1);
            }

            // とは
            if (wordInfos[i].Text == "と" && wordInfos[i + 1].Text == "は")
            {
                wordInfos[i].Text = "とは";
                wordInfos.RemoveAt(i + 1);
            }

            // では
            if (wordInfos[i].Text == "で" && wordInfos[i + 1].Text == "は")
            {
                wordInfos[i].Text = "では";
                wordInfos.RemoveAt(i + 1);
            }

            // のに
            if (wordInfos[i].Text == "の" && wordInfos[i + 1].Text == "に")
            {
                wordInfos[i].Text = "のに";
                wordInfos.RemoveAt(i + 1);
            }
        }

        return wordInfos;
    }

    /// <summary>
    /// Tries to separate the honorifics from the proper names
    /// This still doesn't work for all cases
    /// </summary>
    /// <param name="wordInfos"></param>
    /// <returns></returns>
    private List<WordInfo> SeparateSuffixHonorifics(List<WordInfo> wordInfos)
    {
        for (var i = 0; i < wordInfos.Count; i++)
        {
            foreach (var honorific in HonorificsSuffixes)
            {
                if (!wordInfos[i].Text.EndsWith(honorific) || wordInfos[i].Text.Length <= honorific.Length ||
                    (!wordInfos[i].HasPartOfSpeechSection(PartOfSpeechSection.PersonName) &&
                     !wordInfos[i].HasPartOfSpeechSection(PartOfSpeechSection.ProperNoun))) continue;

                wordInfos[i].Text = wordInfos[i].Text.Substring(0, wordInfos[i].Text.Length - honorific.Length);
                if (wordInfos[i].DictionaryForm.EndsWith(honorific))
                {
                    wordInfos[i].DictionaryForm =
                        wordInfos[i].DictionaryForm.Substring(0, wordInfos[i].DictionaryForm.Length - honorific.Length);
                }

                var suffix = new WordInfo()
                             {
                                 Text = honorific,
                                 PartOfSpeech = PartOfSpeech.Suffix,
                                 Reading = honorific,
                                 DictionaryForm = honorific
                             };
                wordInfos.Insert(i + 1, suffix);
                i++;

                break;
            }
        }

        return wordInfos;
    }

    /// <summary>
    /// Cleanup method / 2nd pass for some cases
    /// </summary>
    /// <param name="wordInfos"></param>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    private List<WordInfo> CombineFinal(List<WordInfo> wordInfos)
    {
        for (int i = 1; i < wordInfos.Count; i++)
        {
            if (wordInfos[i].Text == "ば" &&
                wordInfos[i - 1].PartOfSpeech == PartOfSpeech.Verb)
            {
                wordInfos[i - 1].Text += wordInfos[i].Text;
                wordInfos.RemoveAt(i);
                i--;
            }
        }

        return wordInfos;
    }
}