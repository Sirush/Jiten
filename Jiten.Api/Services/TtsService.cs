using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;
using System.Text.RegularExpressions;
using Jiten.Api.Helpers;
using Jiten.Core;
using Jiten.Core.Data.JMDict;
using Microsoft.EntityFrameworkCore;
using WanaKanaShaapu;

namespace Jiten.Api.Services;

public enum TtsType { Word, Sentence }

public class TtsGenerationLimitException : Exception;
public class TtsTextNotFoundException : Exception;

public interface ITtsService
{
    Task<byte[]> GetWordAudioAsync(int wordId, int readingIndex, string voice, string rateLimitKey, CancellationToken ct, bool bypassGenerationLimit = false);
    Task<byte[]> GetSentenceAudioAsync(int sentenceId, string voice, string rateLimitKey, CancellationToken ct);
    Task<byte[]> GetCustomSentenceAudioAsync(int userExampleSentenceId, string userId, string voice, string rateLimitKey, CancellationToken ct);
}

public class TtsService(
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory,
    IDbContextFactory<JitenDbContext> contextFactory,
    IConfiguration configuration,
    ILogger<TtsService> logger) : ITtsService
{
    private static readonly Dictionary<string, VoiceConfig> Voices = new()
    {
        ["female"] = new("四国めたん", null),
        ["female2"] = new("九州そら", "セクシー", 1.25),
        ["male"] = new("剣崎雌雄", null),
        ["male2"] = new("青山龍星", "しっとり"),
        ["asmr"] = new("九州そら", "ささやき", 1.25),
    };

    private static readonly ConcurrentDictionary<string, int> SpeakerIds = new();
    private static readonly ConcurrentDictionary<string, GenerationCounter> GenCounters = new();

    private const int GenLimitPerMinute = 15;

    private readonly ConcurrentDictionary<string, Task<byte[]>> _inflight = new();
    private readonly string _cdnBaseUrl = configuration.GetValue<string>("CdnBaseUrl") ?? "";

    public async Task<byte[]> GetWordAudioAsync(int wordId, int readingIndex, string voice, string rateLimitKey, CancellationToken ct, bool bypassGenerationLimit = false)
    {
        if (!Voices.ContainsKey(voice)) voice = "female";

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        var ri = (short)readingIndex;
        var wordForms = await db.WordForms
            .AsNoTracking()
            .Where(f => f.WordId == wordId)
            .ToListAsync(ct);

        RubyTextHelper.EnrichForms(wordForms);

        var rubyText = wordForms
            .Where(f => f.ReadingIndex == ri)
            .OrderByDescending(f => f.FormType)
            .Select(f => f.RubyText)
            .FirstOrDefault();


        string? text = !string.IsNullOrEmpty(rubyText) ? RubyToTtsKana(rubyText) : null;
        var usedRuby = !string.IsNullOrWhiteSpace(text) && !ContainsKanji(text);

        if (!usedRuby)
        {
            text = wordForms
                .Where(f => f.FormType == JmDictFormType.KanaForm)
                .OrderBy(f => f.ReadingIndex)
                .Select(f => f.Text)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(text)) throw new TtsTextNotFoundException();
        }

        // OpenJTalk mis-voices a LEADING は/へ/を as a topic/direction/object particle
        // ("wa"/"e"/"o"), but at the head of an isolated word that kana is literal (はで→"hade",
        // not "wade"; 葉→"ha"). Katakana is never treated as a particle, so promote only the
        // leading kana. Interior は/へ/を stay hiragana so genuine particles keep their reading
        // (詳しくは→"kuwashiku wa", ではない→"de wa nai").
        text = FixLeadingParticleKana(text);

        var readingKana = JapaneseTextHelper.ToHiragana(text ?? "");
        var matchingKanjiRubies = wordForms
            .Where(f => f.FormType == JmDictFormType.KanjiForm && !string.IsNullOrEmpty(f.RubyText) && f.RubyText.Contains('['))
            .Where(f => JapaneseTextHelper.ToHiragana(RubyPattern.Replace(f.RubyText, m => m.Groups[1].Value)) == readingKana)
            .Select(f => f.RubyText!)
            .ToList();
        var hasLiteralHa = matchingKanjiRubies.Any(r => r.Contains('は') && !RubyPattern.Replace(r, "").Contains('は'));
        var hasParticleHa = matchingKanjiRubies.Any(r => RubyPattern.Replace(r, "").Contains('は'));

        var fixInteriorHa = !string.IsNullOrEmpty(text)
                            && text.Contains('は') && !text.Contains('わ')
                            && hasLiteralHa && !hasParticleHa;


        int? pitchPosition = null;
        var distinctReadings = wordForms
            .Where(f => f.FormType == JmDictFormType.KanaForm)
            .Select(f => JapaneseTextHelper.ToHiragana(f.Text))
            .Distinct()
            .Count();
        if (distinctReadings == 1)
        {
            var pitches = await db.JMDictWords.AsNoTracking()
                .Where(w => w.WordId == wordId)
                .Select(w => w.PitchAccents)
                .FirstOrDefaultAsync(ct);
            if (pitches is { Count: > 0 }) pitchPosition = pitches[0];
        }

        var storageText = pitchPosition.HasValue ? $"{text}|p{pitchPosition.Value}" : text;
        if (fixInteriorHa) storageText += "|h"; // distinct cache key: audio differs though the text string does not
        var key = $"{voice}:w:{storageText}";
        return await _inflight.GetOrAdd(key, _ => GenerateAsync(key, text, storageText, pitchPosition, TtsType.Word, voice, rateLimitKey, ct, bypassGenerationLimit, fixInteriorHa));
    }

    public async Task<byte[]> GetSentenceAudioAsync(int sentenceId, string voice, string rateLimitKey, CancellationToken ct)
    {
        if (!Voices.ContainsKey(voice)) voice = "female";

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        var text = await db.ExampleSentences
            .Where(s => s.SentenceId == sentenceId)
            .Select(s => s.Text)
            .FirstOrDefaultAsync(ct);

        if (text == null) throw new TtsTextNotFoundException();

        return await SynthesizeSentenceText(text, voice, rateLimitKey, ct);
    }

    public async Task<byte[]> GetCustomSentenceAudioAsync(int userExampleSentenceId, string userId, string voice, string rateLimitKey, CancellationToken ct)
    {
        if (!Voices.ContainsKey(voice)) voice = "female";

        using var scope = scopeFactory.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        var text = await userDb.UserExampleSentences
            .Where(s => s.UserExampleSentenceId == userExampleSentenceId && s.UserId == userId)
            .Select(s => s.Text)
            .FirstOrDefaultAsync(ct);

        if (text == null) throw new TtsTextNotFoundException();

        // Strip the **highlight** markers so the hash (and audio) matches the source example sentence.
        text = text.Replace("**", "");
        if (string.IsNullOrWhiteSpace(text)) throw new TtsTextNotFoundException();

        return await SynthesizeSentenceText(text, voice, rateLimitKey, ct);
    }

    private Task<byte[]> SynthesizeSentenceText(string text, string voice, string rateLimitKey, CancellationToken ct)
    {
        var key = $"{voice}:s:{text}";
        return _inflight.GetOrAdd(key, _ => GenerateSentenceAsync(key, text, voice, rateLimitKey, ct));
    }

    private async Task<string> GetSentenceWithReadings(string text)
    {
        try
        {
            var parsedWords = await Parser.Parser.ParseText(contextFactory, text);
            if (parsedWords.Count == 0) return text;

            var result = new StringBuilder(text);
            var offset = 0;

            foreach (var word in parsedWords)
            {
                if (string.IsNullOrEmpty(word.SudachiReading)) continue;

                var hasKanji = word.OriginalText.Any(c => c >= '\u4E00' && c <= '\u9FFF');
                if (!hasKanji) continue;

                var pos = text.IndexOf(word.OriginalText, offset, StringComparison.Ordinal);
                if (pos < 0) continue;

                var adjustedPos = pos + (result.Length - text.Length);
                result.Remove(adjustedPos, word.OriginalText.Length);
                result.Insert(adjustedPos, word.SudachiReading);
                offset = pos + word.OriginalText.Length;
            }

            return result.ToString();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse sentence for readings, using raw text");
            return text;
        }
    }

    private async Task<byte[]> GenerateSentenceAsync(string key, string rawText, string voice, string rateLimitKey, CancellationToken ct)
    {
        try
        {
            var cached = await TryGetFromCdn(rawText, TtsType.Sentence, voice, ct);
            if (cached != null) return cached;

            var ttsText = await GetSentenceWithReadings(rawText);
            return await SynthesizeAndUpload(key, ttsText, rawText, null, TtsType.Sentence, voice, rateLimitKey, ct);
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    private async Task<byte[]> GenerateAsync(string key, string ttsText, string storageText, int? pitchPosition, TtsType type, string voice, string rateLimitKey, CancellationToken ct, bool bypassGenerationLimit = false, bool fixInteriorHa = false)
    {
        try
        {
            var cached = await TryGetFromCdn(storageText, type, voice, ct);
            if (cached != null) return cached;

            return await SynthesizeAndUpload(key, ttsText, storageText, pitchPosition, type, voice, rateLimitKey, ct, bypassGenerationLimit, fixInteriorHa);
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    private async Task<byte[]?> TryGetFromCdn(string text, TtsType type, string voice, CancellationToken ct)
    {
        var cdnUrl = GetCdnUrl(text, type, voice);
        using var checkClient = httpClientFactory.CreateClient();
        try
        {
            using var headResponse = await checkClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, cdnUrl), ct);
            if (headResponse.IsSuccessStatusCode)
            {
                logger.LogDebug("TTS cache hit: {Text}", text);
                using var getResponse = await checkClient.GetAsync(cdnUrl, ct);
                return await getResponse.Content.ReadAsByteArrayAsync(ct);
            }
        }
        catch { }
        return null;
    }

    private async Task<byte[]> SynthesizeAndUpload(string key, string ttsText, string storageText, int? pitchPosition, TtsType type, string voice, string rateLimitKey, CancellationToken ct, bool bypassGenerationLimit = false, bool fixInteriorHa = false)
    {
        if (!bypassGenerationLimit)
        {
            var counter = GenCounters.GetOrAdd(rateLimitKey, _ => new GenerationCounter());
            if (!counter.TryConsume())
            {
                logger.LogWarning("TTS generation rate limited: {RateLimitKey}", rateLimitKey);
                throw new TtsGenerationLimitException();
            }
        }

        logger.LogInformation("TTS generating: {Text} (voice={Voice}, type={Type})", ttsText, voice, type);
        using var vvClient = httpClientFactory.CreateClient("Voicevox");

        var speakerId = await GetSpeakerId(vvClient, voice, ct);

        var queryResp = await vvClient.PostAsync($"/audio_query?text={Uri.EscapeDataString(ttsText)}&speaker={speakerId}", null, ct);
        queryResp.EnsureSuccessStatusCode();
        var query = await queryResp.Content.ReadFromJsonAsync<JsonElement>(ct);

        var queryDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(query.GetRawText())!;
        var config = Voices[voice];
        queryDict["outputSamplingRate"] = JsonSerializer.SerializeToElement(24000);
        if (config.SpeedScale != 1.0)
            queryDict["speedScale"] = JsonSerializer.SerializeToElement(config.SpeedScale);
        if (type == TtsType.Sentence)
            queryDict["intonationScale"] = JsonSerializer.SerializeToElement(1.5);
        if (fixInteriorHa)
            FixInteriorHaMoras(queryDict);
        if (pitchPosition.HasValue)
        {
            try { await ApplyPitchAccent(vvClient, queryDict, pitchPosition.Value, speakerId, ct); }
            catch (Exception ex) { logger.LogWarning(ex, "Pitch accent override failed, using default accent for {Text}", ttsText); }
        }
        BoostVoicedConsonants(queryDict);

        var synthResp = await vvClient.PostAsJsonAsync($"/synthesis?speaker={speakerId}", queryDict, ct);
        synthResp.EnsureSuccessStatusCode();
        var wavBytes = await synthResp.Content.ReadAsByteArrayAsync(ct);

        var audioBytes = WavToOpus(wavBytes);
        logger.LogInformation("TTS generated: {Text}, {Bytes} bytes", ttsText, audioBytes.Length);

        _ = Task.Run(async () =>
        {
            try
            {
                var storagePath = GetStoragePath(storageText, type, voice);
                using var uploadScope = scopeFactory.CreateScope();
                var cdnService = uploadScope.ServiceProvider.GetRequiredService<ICdnService>();
                await cdnService.UploadFile(audioBytes, storagePath);
                logger.LogInformation("Uploaded TTS to CDN: {StoragePath}", storagePath);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "CDN upload failed for {Text}", ttsText);
            }
        });

        return audioBytes;
    }

    private async Task<int> GetSpeakerId(HttpClient client, string voice, CancellationToken ct)
    {
        if (SpeakerIds.TryGetValue(voice, out var cached))
            return cached;

        var config = Voices[voice];
        var resp = await client.GetAsync("/speakers", ct);
        resp.EnsureSuccessStatusCode();
        var speakers = await resp.Content.ReadFromJsonAsync<JsonElement[]>(ct);

        foreach (var speaker in speakers!)
        {
            if (speaker.GetProperty("name").GetString() != config.Speaker) continue;
            foreach (var style in speaker.GetProperty("styles").EnumerateArray())
            {
                var styleName = style.GetProperty("name").GetString();
                if (config.Style == null || styleName == config.Style)
                {
                    var id = style.GetProperty("id").GetInt32();
                    SpeakerIds[voice] = id;
                    logger.LogInformation("Resolved voice '{Voice}': {Speaker} ({Style}) -> id={Id}", voice, config.Speaker, styleName, id);
                    return id;
                }
            }
        }

        throw new InvalidOperationException($"VOICEVOX speaker '{config.Speaker}' style '{config.Style}' not found");
    }

    private static byte[] WavToOpus(byte[] wavBytes)
    {
        var span = wavBytes.AsSpan();
        var channels = BinaryPrimitives.ReadInt16LittleEndian(span[22..]);
        var sampleRate = BinaryPrimitives.ReadInt32LittleEndian(span[24..]);
        var bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(span[34..]);

        var dataOffset = 12;
        while (dataOffset + 8 < span.Length)
        {
            var chunkId = Encoding.ASCII.GetString(span.Slice(dataOffset, 4));
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(span[(dataOffset + 4)..]);
            if (chunkId == "data")
            {
                dataOffset += 8;
                break;
            }
            dataOffset += 8 + chunkSize;
        }

        var pcmData = span[dataOffset..];
        var sampleCount = pcmData.Length / (bitsPerSample / 8);
        var samples = new short[sampleCount];

        for (var i = 0; i < sampleCount; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcmData[(i * 2)..]);

        using var ms = new MemoryStream();
        var encoder = OpusCodecFactory.CreateEncoder(sampleRate, channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        encoder.Bitrate = 48000;

        var oggStream = new OpusOggWriteStream(encoder, ms);
        oggStream.WriteSamples(samples, 0, samples.Length);
        oggStream.Finish();

        return ms.ToArray();
    }

    private static string GetStoragePath(string text, TtsType type, string voice)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (type == TtsType.Sentence)
        {
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            return $"tts/{voice}/s/{sha}.opus";
        }
        var md5 = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
        return $"tts/{voice}/w/{md5}.opus";
    }

    private string GetCdnUrl(string text, TtsType type, string voice) => $"{_cdnBaseUrl}/{GetStoragePath(text, type, voice)}";

    private static readonly HashSet<string> VoicedConsonants = ["b", "d", "g", "z", "j", "dy", "by", "gy", "zy"];
    private const double VoicedBoostFactor = 2.0;
    private const double VoicedMinLength = 0.1;

    private static void BoostVoicedConsonants(Dictionary<string, JsonElement> queryDict)
    {
        if (!queryDict.TryGetValue("accent_phrases", out var phrasesEl)) return;
        var phrases = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(phrasesEl.GetRawText());
        if (phrases == null) return;

        foreach (var phrase in phrases)
        {
            if (!phrase.TryGetValue("moras", out var morasEl)) continue;
            var moras = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(morasEl.GetRawText());
            if (moras == null) continue;

            foreach (var mora in moras)
            {
                if (!mora.TryGetValue("consonant", out var cEl) || cEl.ValueKind != JsonValueKind.String) continue;
                var consonant = cEl.GetString();
                if (consonant == null || !VoicedConsonants.Contains(consonant)) continue;

                var currentLength = mora.TryGetValue("consonant_length", out var clEl) ? clEl.GetDouble() : 0;
                mora["consonant_length"] = JsonSerializer.SerializeToElement(
                    Math.Max(currentLength * VoicedBoostFactor, VoicedMinLength));
            }

            phrase["moras"] = JsonSerializer.SerializeToElement(moras);
        }

        queryDict["accent_phrases"] = JsonSerializer.SerializeToElement(phrases);
    }

    // Rewrites every "wa" mora (ワ, consonant "w") to "ha" (ハ, consonant "h") in the audio query.
    // Caller guarantees this query came from a reading whose は is a literal kanji reading and that
    // contains no genuine わ, so any ワ mora can only be an interior は that OpenJTalk wrongly voiced
    // as the topic particle. Editing the mora in place keeps the
    // accent/length/pitch VOICEVOX assigned to the natural hiragana, so the prosody is unchanged.
    private static void FixInteriorHaMoras(Dictionary<string, JsonElement> queryDict)
    {
        if (!queryDict.TryGetValue("accent_phrases", out var phrasesEl)) return;
        var phrases = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(phrasesEl.GetRawText());
        if (phrases == null) return;

        foreach (var phrase in phrases)
        {
            if (!phrase.TryGetValue("moras", out var morasEl)) continue;
            var moras = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(morasEl.GetRawText());
            if (moras == null) continue;

            foreach (var mora in moras)
            {
                if (!mora.TryGetValue("text", out var tEl) || tEl.ValueKind != JsonValueKind.String || tEl.GetString() != "ワ") continue;
                mora["text"] = JsonSerializer.SerializeToElement("ハ");
                mora["consonant"] = JsonSerializer.SerializeToElement("h");
            }

            phrase["moras"] = JsonSerializer.SerializeToElement(moras);
        }

        queryDict["accent_phrases"] = JsonSerializer.SerializeToElement(phrases);
    }

    private static async Task ApplyPitchAccent(HttpClient client, Dictionary<string, JsonElement> queryDict, int position, int speakerId, CancellationToken ct)
    {
        if (!queryDict.TryGetValue("accent_phrases", out var phrasesEl) || phrasesEl.ValueKind != JsonValueKind.Array) return;

        var moras = new List<JsonElement>();
        foreach (var phrase in phrasesEl.EnumerateArray())
            if (phrase.TryGetProperty("moras", out var morasEl))
                moras.AddRange(morasEl.EnumerateArray());

        if (moras.Count == 0) return;
        var accent = position <= 0 ? moras.Count : Math.Min(position, moras.Count);

        var body = new[]
        {
            new Dictionary<string, object?>
            {
                ["moras"] = moras,
                ["accent"] = accent,
                ["pause_mora"] = null,
                ["is_interrogative"] = false,
            },
        };

        var resp = await client.PostAsJsonAsync($"/mora_pitch?speaker={speakerId}", body, ct);
        resp.EnsureSuccessStatusCode();
        queryDict["accent_phrases"] = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private static readonly Regex RubyPattern = new(@"[\u4E00-\u9FFF\uFF10-\uFF5A々]+\[([\u3040-\u309F\u30A0-\u30FF]+)\]", RegexOptions.Compiled);

    // Replaces each kanji block with its furigana reading, leaving the literal kana around it
    // (okurigana, particles) untouched. The reading is emitted as hiragana so OpenJTalk keeps its
    // correct long-vowel handling (どう→"dō"); katakana ドウ would be read "do-u" with the moras
    // split apart. The sole exception is a single-mora は/へ/を reading (e.g. 葉→は), which OpenJTalk
    // would otherwise voice as a topic/direction/object particle — that one is promoted to katakana.
    private static string RubyToTtsKana(string rubyText) =>
        RubyPattern.Replace(rubyText, m =>
        {
            var reading = m.Groups[1].Value;
            return reading is "は" or "へ" or "を" ? WanaKana.ToKatakana(reading) : reading;
        });

    private static bool ContainsKanji(string text) =>
        text.Any(c => (c >= '一' && c <= '鿿') || c == '々');

    private static string FixLeadingParticleKana(string text) =>
        text.Length > 0 && text[0] is 'は' or 'へ' or 'を'
            ? WanaKana.ToKatakana(text[0].ToString()) + text[1..]
            : text;

    private record VoiceConfig(string Speaker, string? Style, double SpeedScale = 1.0);

    private class GenerationCounter
    {
        private int _count;
        private long _windowStart = Environment.TickCount64;

        public bool TryConsume()
        {
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _windowStart) > 60_000)
            {
                Interlocked.Exchange(ref _count, 0);
                Interlocked.Exchange(ref _windowStart, now);
            }
            return Interlocked.Increment(ref _count) <= GenLimitPerMinute;
        }
    }
}
