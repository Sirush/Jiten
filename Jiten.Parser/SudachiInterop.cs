using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using Jiten.Core.Utils;

namespace Jiten.Parser;

static class SudachiInterop
{
    // Existing delegates
    private delegate IntPtr RunCliFfiDelegate(string configPath, string filePath, string dictionaryPath, string outputPath);

    private delegate IntPtr ProcessTextFfiDelegate(string configPath, IntPtr inputText, string dictionaryPath, char mode, bool printAll,
                                                   bool wakati);

    private delegate void FreeStringDelegate(IntPtr ptr);

    // Streaming callback delegate: receives raw UTF-8 bytes from Sudachi
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate void OutputCallback(IntPtr userData, byte* data, nuint len);

    // Context management delegates
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CreateContextDelegate(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string configPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string dictionaryPath,
        out IntPtr ctx);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeContextDelegate(IntPtr ctx);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CreateContextWithUserCsvDelegate(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string configPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string dictionaryPath,
        IntPtr csvPtr,
        nuint csvLen,
        out IntPtr ctx);

    // Streaming processor delegate
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate IntPtr ProcessTextCtxStreamUtf8V2Delegate(
        IntPtr ctx,
        byte* inputPtr,
        nuint inputLen,
        sbyte modeChar,
        byte printAll,
        byte wakati,
        OutputCallback callback,
        IntPtr userData);

    // v3: adds emit_margins — when 1, each token line gains a trailing "\tM=<int>" segmentation
    // margin column (extra cost of the cheapest competing lattice path crossing a token boundary)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate IntPtr ProcessTextCtxStreamUtf8V3Delegate(
        IntPtr ctx,
        byte* inputPtr,
        nuint inputLen,
        sbyte modeChar,
        byte printAll,
        byte wakati,
        byte emitMargins,
        OutputCallback callback,
        IntPtr userData);

    // Static callback delegate instance to prevent GC during native calls
    private static readonly unsafe OutputCallback _outputCallback = OnSudachiOutput;

    private static RunCliFfiDelegate _runCliFfi = null!;
    private static ProcessTextFfiDelegate _processTextFfi = null!;
    private static FreeStringDelegate _freeString = null!;

    // Streaming FFI delegates (optional, for newer library versions)
    private static CreateContextDelegate? _createContext;
    private static FreeContextDelegate? _freeContext;
    private static ProcessTextCtxStreamUtf8V2Delegate? _processTextCtxStreamV2;
    private static ProcessTextCtxStreamUtf8V3Delegate? _processTextCtxStreamV3;
    private static CreateContextWithUserCsvDelegate? _createContextWithUserCsv;

    private static readonly IntPtr _libHandle;

    // Context management (lazy, reusable with periodic recycling to prevent native memory growth)
    // Keyed by configPath so callers can alternate configs (e.g. with/without user dictionary)
    private static readonly Dictionary<string, IntPtr> _sudachiContexts = new();
    private static long _contextCallCount;
    private const long ContextRecycleThreshold = 5_000;

    // Dynamic user dictionary CSV bytes (set per-deck, used at context creation)
    private static byte[]? _dynamicUserDictCsv;

    // Limit concurrent Sudachi processing to match parse worker count (ProcessorCount / 4, min 1)
    // This reduces lock contention by gating how many threads attempt to acquire ProcessTextLock
    private static readonly int MaxConcurrentProcessing = Math.Max(1, Environment.ProcessorCount / 4);
    private static readonly SemaphoreSlim _processingSemaphore = new(MaxConcurrentProcessing, MaxConcurrentProcessing);

    // Thread-static callback state
    [ThreadStatic] private static byte[]? _leftover;
    [ThreadStatic] private static int _leftoverLen;
    [ThreadStatic] private static List<WordInfo>? _wordInfos;
    [ThreadStatic] private static Exception? _cbError;
    [ThreadStatic] private static StringBuilder? _rawCapture;

    // Precomputed lookup table for allowed characters (replaces expensive regex)
    private static readonly bool[] _allowedChars = BuildAllowedCharsTable();

    private static bool[] BuildAllowedCharsTable()
    {
        var table = new bool[65536];

        // Hiragana
        for (int c = 0x3040; c <= 0x309F; c++) table[c] = true;
        // Katakana
        for (int c = 0x30A0; c <= 0x30FF; c++) table[c] = true;
        // CJK Unified Ideographs
        for (int c = 0x4E00; c <= 0x9FAF; c++) table[c] = true;
        // Fullwidth Latin Capital Letters (A-Z)
        for (int c = 0xFF21; c <= 0xFF3A; c++) table[c] = true;
        // Fullwidth Latin Small Letters (a-z)
        for (int c = 0xFF41; c <= 0xFF5A; c++) table[c] = true;
        // Fullwidth Digits (0-9)
        for (int c = 0xFF10; c <= 0xFF19; c++) table[c] = true;
        // Ideographic Iteration Mark (々)
        table[0x3005] = true;
        // CJK Punctuation (、。〃)
        for (int c = 0x3001; c <= 0x3003; c++) table[c] = true;
        // CJK Brackets (〈〉《》「」『』【】)
        for (int c = 0x3008; c <= 0x3011; c++) table[c] = true;
        // More CJK Brackets/Punctuation
        for (int c = 0x3014; c <= 0x301F; c++) table[c] = true;
        // Fullwidth Punctuation (！＂＃＄％＆＇（）＊＋，－．／)
        for (int c = 0xFF01; c <= 0xFF0F; c++) table[c] = true;
        // More Fullwidth Punctuation (：；＜＝＞？)
        for (int c = 0xFF1A; c <= 0xFF1F; c++) table[c] = true;
        // Fullwidth Brackets (［＼］＾＿)
        for (int c = 0xFF3B; c <= 0xFF3F; c++) table[c] = true;
        // Fullwidth Braces (｛｜｝～)
        for (int c = 0xFF5B; c <= 0xFF60; c++) table[c] = true;
        // Halfwidth Katakana Punctuation
        for (int c = 0xFF62; c <= 0xFF65; c++) table[c] = true;
        // Pipe (used as batch delimiter and stop token by MorphologicalAnalyser)
        table['|'] = true;
        // Newline
        table['\n'] = true;
        // Horizontal Ellipsis (…)
        table[0x2026] = true;
        // Ideographic Space
        table[0x3000] = true;
        // Horizontal Bar (―)
        table[0x2015] = true;
        // Box Drawing Light Horizontal (─)
        table[0x2500] = true;
        // Parentheses
        table['('] = true;
        table[')'] = true;
        // Space
        table[' '] = true;
        // Vertical Bar
        table['|'] = true;

        return table;
    }

    private static bool HasNoJapaneseChars(string text)
    {
        foreach (char c in text)
        {
            if ((c >= 0x3040 && c <= 0x309F) ||
                (c >= 0x30A0 && c <= 0x30FF) ||
                (c >= 0x4E00 && c <= 0x9FAF) ||
                (c >= 0xFF21 && c <= 0xFF3A) ||
                (c >= 0xFF41 && c <= 0xFF5A))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Fast character filter using lookup table and ArrayPool.
    /// Returns original string if no characters were removed (fast path).
    /// </summary>
    private static string FilterAllowedChars(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        // First pass: check if any characters need to be removed
        int removeCount = 0;
        foreach (char c in input)
        {
            if (c >= 65536 || !_allowedChars[c])
                removeCount++;
        }

        // Fast path: no characters to remove
        if (removeCount == 0)
            return input;

        // Rent a buffer from the pool
        int outputLen = input.Length - removeCount;
        char[] buffer = ArrayPool<char>.Shared.Rent(outputLen);

        try
        {
            int j = 0;
            foreach (char c in input)
            {
                if (c < 65536 && _allowedChars[c])
                    buffer[j++] = c;
            }

            return new string(buffer, 0, j);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private static string GetSudachiLibPath()
    {
        string basePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Path.Combine(basePath, "sudachi_lib.dll");
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return Path.Combine(basePath, "libsudachi_lib.so");
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return Path.Combine(basePath, "libsudachi_lib.dylib");
        else
            throw new PlatformNotSupportedException("Unsupported platform");
    }

    static SudachiInterop()
    {
        // Load the appropriate native library for the current platform
        _libHandle = NativeLibrary.Load(GetSudachiLibPath());

        // Get function pointers for existing exports
        IntPtr runCliFfiPtr = NativeLibrary.GetExport(_libHandle, "run_cli_ffi");
        IntPtr processTextFfiPtr = NativeLibrary.GetExport(_libHandle, "process_text_ffi");
        IntPtr freeStringPtr = NativeLibrary.GetExport(_libHandle, "free_string");

        // Create delegates from function pointers
        _runCliFfi = Marshal.GetDelegateForFunctionPointer<RunCliFfiDelegate>(runCliFfiPtr);
        _processTextFfi = Marshal.GetDelegateForFunctionPointer<ProcessTextFfiDelegate>(processTextFfiPtr);
        _freeString = Marshal.GetDelegateForFunctionPointer<FreeStringDelegate>(freeStringPtr);

        // New streaming exports (optional, for newer library versions)
        if (NativeLibrary.TryGetExport(_libHandle, "create_context_ffi", out IntPtr createCtxPtr))
            _createContext = Marshal.GetDelegateForFunctionPointer<CreateContextDelegate>(createCtxPtr);
        if (NativeLibrary.TryGetExport(_libHandle, "process_text_ctx_stream_utf8_ffi_v2", out IntPtr streamV2Ptr))
            _processTextCtxStreamV2 = Marshal.GetDelegateForFunctionPointer<ProcessTextCtxStreamUtf8V2Delegate>(streamV2Ptr);
        if (NativeLibrary.TryGetExport(_libHandle, "process_text_ctx_stream_utf8_ffi_v3", out IntPtr streamV3Ptr))
            _processTextCtxStreamV3 = Marshal.GetDelegateForFunctionPointer<ProcessTextCtxStreamUtf8V3Delegate>(streamV3Ptr);
        if (NativeLibrary.TryGetExport(_libHandle, "free_context_ffi", out IntPtr freeCtxPtr))
            _freeContext = Marshal.GetDelegateForFunctionPointer<FreeContextDelegate>(freeCtxPtr);
        if (NativeLibrary.TryGetExport(_libHandle, "create_context_with_user_csv_ffi", out IntPtr createCtxCsvPtr))
            _createContextWithUserCsv = Marshal.GetDelegateForFunctionPointer<CreateContextWithUserCsvDelegate>(createCtxCsvPtr);
    }

    private static readonly object ProcessTextLock = new object();


    public static string RunCli(string configPath, string filePath, string dictionaryPath, string outputPath)
    {
        // Call the FFI function
        IntPtr resultPtr = _runCliFfi(configPath, filePath, dictionaryPath, outputPath);

        // Convert the result to a C# string
        string result = Marshal.PtrToStringAnsi(resultPtr) ?? string.Empty;

        // Free the string allocated in Rust
        _freeString(resultPtr);

        return result;
    }

    public static string ProcessText(string configPath, string inputText, string dictionaryPath, char mode = 'C', bool printAll = true,
                                     bool wakati = false)
    {
        _processingSemaphore.Wait();
        try
        {
            lock (ProcessTextLock)
            {
                // Clean up text using fast lookup table filter
                inputText = FilterAllowedChars(inputText);

                // if there's no kanas, kanjis, or fullwidth letters, abort
                if (HasNoJapaneseChars(inputText))
                    return "";

                byte[] inputBytes = Encoding.UTF8.GetBytes(inputText + "\0");
                IntPtr inputTextPtr = Marshal.AllocHGlobal(inputBytes.Length);
                Marshal.Copy(inputBytes, 0, inputTextPtr, inputBytes.Length);

                IntPtr resultPtr = _processTextFfi(configPath, inputTextPtr, dictionaryPath, mode, printAll, wakati);
                string result = Marshal.PtrToStringUTF8(resultPtr) ?? string.Empty;

                _freeString(resultPtr);

                Marshal.FreeHGlobal(inputTextPtr);

                return result;
            }
        }
        finally
        {
            _processingSemaphore.Release();
        }
    }

    /// <summary>
    /// Indicates whether the streaming FFI is available in the loaded native library.
    /// </summary>
    public static bool StreamingAvailable => _processTextCtxStreamV2 != null;

    /// <summary>
    /// Process text using streaming FFI, parsing WordInfo objects incrementally without building the full output string.
    /// When userDictCsv is provided, the Sudachi context is rebuilt with the extra dictionary entries.
    /// </summary>
    public static List<WordInfo> ProcessTextStreaming(
        string configPath,
        string inputText,
        string dictionaryPath,
        char mode = 'C',
        bool printAll = true,
        bool wakati = false,
        byte[]? userDictCsv = null,
        bool emitMargins = false)
    {
        return ProcessTextStreaming(configPath, inputText, dictionaryPath, out _, captureRaw: false,
                                    mode, printAll, wakati, userDictCsv, emitMargins);
    }

    public static List<WordInfo> ProcessTextStreaming(
        string configPath,
        string inputText,
        string dictionaryPath,
        out string? rawOutput,
        bool captureRaw,
        char mode = 'C',
        bool printAll = true,
        bool wakati = false,
        byte[]? userDictCsv = null,
        bool emitMargins = false)
    {
        rawOutput = null;
        if (_processTextCtxStreamV2 == null)
            throw new InvalidOperationException("Streaming FFI not available in this build");
        if (emitMargins && _processTextCtxStreamV3 == null)
            emitMargins = false; // old native library without margin support

        _processingSemaphore.Wait();
        try
        {
            lock (ProcessTextLock)
            {
                if (!CsvUnchanged(_dynamicUserDictCsv, userDictCsv))
                {
                    _dynamicUserDictCsv = userDictCsv;
                    RecycleContext();
                }
                // Clean up text using fast lookup table filter
                inputText = FilterAllowedChars(inputText);

                // If there's no kanas, kanjis, or fullwidth letters, abort
                if (HasNoJapaneseChars(inputText))
                    return new List<WordInfo>();

                ResetCallbackState(captureRaw);

                IntPtr ctx = GetOrCreateContext(configPath, dictionaryPath);

                byte[] inputBytes = Encoding.UTF8.GetBytes(inputText);

                unsafe
                {
                    fixed (byte* inputPtr = inputBytes)
                    {
                        IntPtr errPtr = emitMargins
                            ? _processTextCtxStreamV3!(
                                ctx,
                                inputPtr,
                                (nuint)inputBytes.Length,
                                (sbyte)mode,
                                (byte)(printAll ? 1 : 0),
                                (byte)(wakati ? 1 : 0),
                                1,
                                _outputCallback,
                                IntPtr.Zero)
                            : _processTextCtxStreamV2(
                                ctx,
                                inputPtr,
                                (nuint)inputBytes.Length,
                                (sbyte)mode,
                                (byte)(printAll ? 1 : 0),
                                (byte)(wakati ? 1 : 0),
                                _outputCallback,
                                IntPtr.Zero);

                        string err = Marshal.PtrToStringUTF8(errPtr) ?? "";
                        _freeString(errPtr);

                        if (!string.IsNullOrEmpty(err))
                        {
                            RecycleContext();
                            throw new InvalidOperationException($"Sudachi streaming error: {err}");
                        }
                    }
                }

                // Flush any remaining leftover
                if (_leftoverLen > 0)
                {
                    string line = Encoding.UTF8.GetString(_leftover!, 0, _leftoverLen);
                    if (line != "EOS" && line.Length != 0)
                    {
                        _rawCapture?.Append(line).Append('\n');
                        var wi = new WordInfo(line);
                        if (!wi.IsInvalid) _wordInfos!.Add(wi);
                    }
                }

                if (_cbError != null)
                    throw new InvalidOperationException("Sudachi streaming callback error", _cbError);

                rawOutput = _rawCapture?.ToString();
                _rawCapture = null;

                var result = _wordInfos!;
                _wordInfos = null;

                if (Interlocked.Increment(ref _contextCallCount) % ContextRecycleThreshold == 0)
                    RecycleContext();

                return result;
            }
        }
        finally
        {
            _processingSemaphore.Release();
        }
    }

    private static void RecycleContext()
    {
        if (_freeContext != null)
        {
            foreach (var ctx in _sudachiContexts.Values)
                if (ctx != IntPtr.Zero)
                    _freeContext(ctx);
        }
        _sudachiContexts.Clear();
    }

    private static bool CsvUnchanged(byte[]? a, byte[]? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null || a.Length != b.Length) return false;
        return a.AsSpan().SequenceEqual(b);
    }

    private static IntPtr GetOrCreateContext(string configPath, string dictionaryPath)
    {
        if (_sudachiContexts.TryGetValue(configPath, out var existing) && existing != IntPtr.Zero)
            return existing;

        IntPtr errPtr;
        IntPtr ctx;

        if (_dynamicUserDictCsv is { Length: > 0 } && _createContextWithUserCsv != null)
        {
            var handle = GCHandle.Alloc(_dynamicUserDictCsv, GCHandleType.Pinned);
            try
            {
                errPtr = _createContextWithUserCsv(
                    configPath, dictionaryPath,
                    handle.AddrOfPinnedObject(), (nuint)_dynamicUserDictCsv.Length,
                    out ctx);
            }
            finally
            {
                handle.Free();
            }
        }
        else if (_createContext != null)
        {
            errPtr = _createContext(configPath, dictionaryPath, out ctx);
        }
        else
        {
            return IntPtr.Zero;
        }

        string err = Marshal.PtrToStringUTF8(errPtr) ?? "";
        _freeString(errPtr);

        if (!string.IsNullOrEmpty(err) || ctx == IntPtr.Zero)
            throw new InvalidOperationException(err.Length != 0 ? err : "Failed to create Sudachi context");

        _sudachiContexts[configPath] = ctx;
        return ctx;
    }

    /// <summary>
    /// Cleanup the Sudachi contexts. Call on application shutdown.
    /// </summary>
    public static void Cleanup()
    {
        RecycleContext();
    }

    private static void ResetCallbackState(bool captureRaw = false)
    {
        _leftover ??= new byte[4096];
        _leftoverLen = 0;
        _wordInfos = new List<WordInfo>();
        _cbError = null;
        _rawCapture = captureRaw ? new StringBuilder() : null;
    }

    private static unsafe void OnSudachiOutput(IntPtr _, byte* data, nuint len)
    {
        try
        {
            var span = new ReadOnlySpan<byte>(data, checked((int)len));
            int i = 0;

            while (true)
            {
                int nl = span.Slice(i).IndexOf((byte)'\n');
                if (nl < 0) break;

                ReadOnlySpan<byte> part = span.Slice(i, nl);
                string line;

                if (_leftoverLen != 0)
                {
                    var tmp = new byte[_leftoverLen + part.Length];
                    Buffer.BlockCopy(_leftover!, 0, tmp, 0, _leftoverLen);
                    part.CopyTo(tmp.AsSpan(_leftoverLen));
                    _leftoverLen = 0;
                    line = Encoding.UTF8.GetString(tmp);
                }
                else
                {
                    line = Encoding.UTF8.GetString(part);
                }

                if (line != "EOS" && line.Length != 0)
                {
                    _rawCapture?.Append(line).Append('\n');
                    var wi = new WordInfo(line);
                    if (!wi.IsInvalid) _wordInfos!.Add(wi);
                }

                i += nl + 1;
            }

            // Store leftover bytes for next callback
            var tail = span.Slice(i);
            if (!tail.IsEmpty)
            {
                if (_leftover!.Length < _leftoverLen + tail.Length)
                    Array.Resize(ref _leftover, Math.Max(_leftoverLen + tail.Length, _leftoverLen * 2 + 1024));
                tail.CopyTo(_leftover.AsSpan(_leftoverLen));
                _leftoverLen += tail.Length;
            }
        }
        catch (Exception ex)
        {
            _cbError = ex;
        }
    }
}
