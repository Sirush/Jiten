using System.Buffers;
using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using System.Runtime.InteropServices;
using System.Text;
using Jiten.Core.Utils;

namespace Jiten.Parser;

static class SudachiInterop
{
    private delegate IntPtr RunCliFfiDelegate(string configPath, string filePath, string dictionaryPath, string outputPath);

    private delegate IntPtr ProcessTextFfiDelegate(string configPath, IntPtr inputText, string dictionaryPath, char mode, bool printAll,
                                                   bool wakati);

    private delegate void FreeStringDelegate(IntPtr ptr);

    // Receives raw UTF-8 bytes; a chunk may end mid-line.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate void OutputCallback(IntPtr userData, byte* data, nuint len);

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

    // emit_margins=1 appends "\tM=<int>" per token: extra cost of the cheapest rival lattice path crossing its boundary.
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

    // Static so the GC never collects the delegate while native code holds its pointer.
    private static readonly unsafe OutputCallback _outputCallback = OnSudachiOutput;

    private static RunCliFfiDelegate _runCliFfi = null!;
    private static ProcessTextFfiDelegate _processTextFfi = null!;
    private static FreeStringDelegate _freeString = null!;

    // Null when the loaded native library predates the export.
    private static CreateContextDelegate? _createContext;
    private static FreeContextDelegate? _freeContext;
    private static ProcessTextCtxStreamUtf8V2Delegate? _processTextCtxStreamV2;
    private static ProcessTextCtxStreamUtf8V3Delegate? _processTextCtxStreamV3;
    private static CreateContextWithUserCsvDelegate? _createContextWithUserCsv;

    private static readonly IntPtr _libHandle;

    // A slot owns its contexts and the user-dict CSV they were built with; a per-deck swap rebuilds only that slot.
    private sealed class ContextSlot
    {
        public readonly Dictionary<string, IntPtr> Contexts = new();
        public byte[]? UserDictCsv;
        public long CallCount;
    }

    private const long ContextRecycleThreshold = 5_000;

    // Request-path callers own InteractiveSlots and may borrow an idle bulk slot; bulk callers never take an interactive one.
    private static readonly int BulkSlots;
    private static readonly int InteractiveSlots;
    private static readonly SemaphoreSlim _bulkGate;
    private static readonly SemaphoreSlim _interactiveGate;
    private static readonly ConcurrentStack<ContextSlot> _freeSlots = new();

    // Passed to the native side as userData; concurrent calls must never share callback state.
    private sealed class CallbackState(bool captureRaw)
    {
        public byte[] Leftover = new byte[4096];
        public int LeftoverLen;
        public readonly List<WordInfo> WordInfos = new();
        public readonly SudachiStringPool Strings = new();
        public Exception? Error;
        public readonly StringBuilder? RawCapture = captureRaw ? new StringBuilder() : null;
    }

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
        // Fullwidth A-Z
        for (int c = 0xFF21; c <= 0xFF3A; c++) table[c] = true;
        // Fullwidth a-z
        for (int c = 0xFF41; c <= 0xFF5A; c++) table[c] = true;
        // Fullwidth 0-9
        for (int c = 0xFF10; c <= 0xFF19; c++) table[c] = true;
        // 々
        table[0x3005] = true;
        // 、。〃
        for (int c = 0x3001; c <= 0x3003; c++) table[c] = true;
        // 〈〉《》「」『』【】
        for (int c = 0x3008; c <= 0x3011; c++) table[c] = true;
        // 〔〕〖〗〘〙〚〛〜〝〞〟
        for (int c = 0x3014; c <= 0x301F; c++) table[c] = true;
        // ！＂＃＄％＆＇（）＊＋，－．／
        for (int c = 0xFF01; c <= 0xFF0F; c++) table[c] = true;
        // ：；＜＝＞？
        for (int c = 0xFF1A; c <= 0xFF1F; c++) table[c] = true;
        // ［＼］＾＿
        for (int c = 0xFF3B; c <= 0xFF3F; c++) table[c] = true;
        // ｛｜｝～｟｠
        for (int c = 0xFF5B; c <= 0xFF60; c++) table[c] = true;
        // Halfwidth ｢｣､･
        for (int c = 0xFF62; c <= 0xFF65; c++) table[c] = true;
        // MorphologicalAnalyser uses | as batch delimiter and stop token.
        table['|'] = true;
        table['\n'] = true;
        // …
        table[0x2026] = true;
        // Ideographic space
        table[0x3000] = true;
        // ―
        table[0x2015] = true;
        // ─
        table[0x2500] = true;
        table['('] = true;
        table[')'] = true;
        table[' '] = true;
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

    /// <summary>Returns the same instance when nothing is removed.</summary>
    internal static string FilterAllowedChars(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        int removeCount = 0;
        foreach (char c in input)
        {
            if (c >= 65536 || !_allowedChars[c])
                removeCount++;
        }

        if (removeCount == 0)
            return input;

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
        _libHandle = NativeLibrary.Load(GetSudachiLibPath());

        IntPtr runCliFfiPtr = NativeLibrary.GetExport(_libHandle, "run_cli_ffi");
        IntPtr processTextFfiPtr = NativeLibrary.GetExport(_libHandle, "process_text_ffi");
        IntPtr freeStringPtr = NativeLibrary.GetExport(_libHandle, "free_string");

        _runCliFfi = Marshal.GetDelegateForFunctionPointer<RunCliFfiDelegate>(runCliFfiPtr);
        _processTextFfi = Marshal.GetDelegateForFunctionPointer<ProcessTextFfiDelegate>(processTextFfiPtr);
        _freeString = Marshal.GetDelegateForFunctionPointer<FreeStringDelegate>(freeStringPtr);

        // Optional exports; older native builds lack them.
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

        var config = Runtime.ParserRuntimeSettings.Current.Configuration;
        BulkSlots = Math.Max(1, config.GetValue("Parser:SudachiBulkContexts", Math.Max(2, Environment.ProcessorCount / 4)));
        InteractiveSlots = Math.Max(0, config.GetValue("Parser:SudachiInteractiveContexts", 1));
        _bulkGate = new SemaphoreSlim(BulkSlots, BulkSlots);
        _interactiveGate = new SemaphoreSlim(InteractiveSlots, Math.Max(1, InteractiveSlots));
        for (int i = 0; i < BulkSlots + InteractiveSlots; i++)
            _freeSlots.Push(new ContextSlot());
    }

    private static SemaphoreSlim AcquireGate(bool interactive)
    {
        if (interactive && InteractiveSlots > 0)
        {
            if (_interactiveGate.Wait(0)) return _interactiveGate;
            if (_bulkGate.Wait(0)) return _bulkGate;
            _interactiveGate.Wait();
            return _interactiveGate;
        }
        _bulkGate.Wait();
        return _bulkGate;
    }

    private static async Task<SemaphoreSlim> AcquireGateAsync(bool interactive, CancellationToken cancellationToken)
    {
        if (interactive && InteractiveSlots > 0)
        {
            if (_interactiveGate.Wait(0)) return _interactiveGate;
            if (_bulkGate.Wait(0)) return _bulkGate;
            await _interactiveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return _interactiveGate;
        }
        await _bulkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return _bulkGate;
    }

    /// <summary>Builds the default-dictionary context in every slot so no caller pays the first-use load.</summary>
    public static void WarmPool(string configPath, string dictionaryPath, Action<string>? log = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var slots = new List<ContextSlot>();
        while (_freeSlots.TryPop(out var slot))
            slots.Add(slot);
        try
        {
            foreach (var slot in slots)
                if (slot.UserDictCsv == null)
                    GetOrCreateContext(slot, configPath, dictionaryPath);
        }
        finally
        {
            foreach (var slot in slots)
                _freeSlots.Push(slot);
        }
        log?.Invoke($"Sudachi pool warmed: {slots.Count} contexts in {sw.ElapsedMilliseconds}ms");
    }

    // Permits equal slots, so a miss only means WarmPool is holding them briefly.
    private static ContextSlot TakeSlot()
    {
        var spin = new SpinWait();
        ContextSlot? slot;
        while (!_freeSlots.TryPop(out slot))
            spin.SpinOnce();
        return slot;
    }

    private static void ReturnSlot(ContextSlot slot, SemaphoreSlim gate)
    {
        _freeSlots.Push(slot);
        gate.Release();
    }


    public static string RunCli(string configPath, string filePath, string dictionaryPath, string outputPath)
    {
        IntPtr resultPtr = _runCliFfi(configPath, filePath, dictionaryPath, outputPath);

        string result = Marshal.PtrToStringAnsi(resultPtr) ?? string.Empty;

        // Rust-allocated; must be freed by the library, not Marshal.
        _freeString(resultPtr);

        return result;
    }

    public static string ProcessText(string configPath, string inputText, string dictionaryPath, char mode = 'C', bool printAll = true,
                                     bool wakati = false)
    {
        var gate = AcquireGate(interactive: false);
        var slot = TakeSlot();
        try
        {
            inputText = FilterAllowedChars(inputText);

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
        finally
        {
            ReturnSlot(slot, gate);
        }
    }

    public static bool StreamingAvailable => _processTextCtxStreamV2 != null;

    /// <summary>A userDictCsv different from the slot's rebuilds that slot's Sudachi contexts.</summary>
    public static List<WordInfo> ProcessTextStreaming(
        string configPath,
        string inputText,
        string dictionaryPath,
        char mode = 'C',
        bool printAll = true,
        bool wakati = false,
        byte[]? userDictCsv = null,
        bool emitMargins = false,
        bool interactive = false)
    {
        return ProcessTextStreaming(configPath, inputText, dictionaryPath, out _, captureRaw: false,
                                    mode, printAll, wakati, userDictCsv, emitMargins, interactive);
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
        bool emitMargins = false,
        bool interactive = false)
    {
        var gate = AcquireGate(interactive);
        var slot = TakeSlot();
        try
        {
            return ProcessTextStreamingCore(slot, configPath, inputText, dictionaryPath, out rawOutput, captureRaw,
                                            mode, printAll, wakati, userDictCsv, emitMargins);
        }
        finally
        {
            ReturnSlot(slot, gate);
        }
    }

    /// <summary>Awaits a pool slot instead of blocking a thread-pool thread on it.</summary>
    public static async Task<(List<WordInfo> Words, string? RawOutput)> ProcessTextStreamingAsync(
        string configPath,
        string inputText,
        string dictionaryPath,
        bool captureRaw = false,
        char mode = 'C',
        bool printAll = true,
        bool wakati = false,
        byte[]? userDictCsv = null,
        bool emitMargins = false,
        bool interactive = false,
        CancellationToken cancellationToken = default)
    {
        var gate = await AcquireGateAsync(interactive, cancellationToken).ConfigureAwait(false);
        var slot = TakeSlot();
        try
        {
            var words = ProcessTextStreamingCore(slot, configPath, inputText, dictionaryPath, out var rawOutput, captureRaw,
                                                 mode, printAll, wakati, userDictCsv, emitMargins);
            return (words, rawOutput);
        }
        finally
        {
            ReturnSlot(slot, gate);
        }
    }

    private static List<WordInfo> ProcessTextStreamingCore(
        ContextSlot slot,
        string configPath,
        string inputText,
        string dictionaryPath,
        out string? rawOutput,
        bool captureRaw,
        char mode,
        bool printAll,
        bool wakati,
        byte[]? userDictCsv,
        bool emitMargins)
    {
        rawOutput = null;
        if (_processTextCtxStreamV2 == null)
            throw new InvalidOperationException("Streaming FFI not available in this build");
        if (emitMargins && _processTextCtxStreamV3 == null)
            emitMargins = false; // native library predates margin support

        {
            if (!CsvUnchanged(slot.UserDictCsv, userDictCsv))
            {
                slot.UserDictCsv = userDictCsv;
                RecycleContext(slot);
            }
            inputText = FilterAllowedChars(inputText);

            if (HasNoJapaneseChars(inputText))
                return new List<WordInfo>();

            var state = new CallbackState(captureRaw);
            var stateHandle = GCHandle.Alloc(state);

            IntPtr ctx = GetOrCreateContext(slot, configPath, dictionaryPath);

            byte[] inputBytes = Encoding.UTF8.GetBytes(inputText);

            try
            {
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
                                GCHandle.ToIntPtr(stateHandle))
                            : _processTextCtxStreamV2(
                                ctx,
                                inputPtr,
                                (nuint)inputBytes.Length,
                                (sbyte)mode,
                                (byte)(printAll ? 1 : 0),
                                (byte)(wakati ? 1 : 0),
                                _outputCallback,
                                GCHandle.ToIntPtr(stateHandle));

                        string err = Marshal.PtrToStringUTF8(errPtr) ?? "";
                        _freeString(errPtr);

                        if (!string.IsNullOrEmpty(err))
                        {
                            RecycleContext(slot);
                            throw new InvalidOperationException($"Sudachi streaming error: {err}");
                        }
                    }
                }
            }
            finally
            {
                stateHandle.Free();
            }

            // Output may not end with a newline.
            if (state.LeftoverLen > 0)
            {
                ReadOnlySpan<byte> line = state.Leftover.AsSpan(0, state.LeftoverLen);
                if (!line.SequenceEqual("EOS"u8))
                {
                    state.RawCapture?.Append(Encoding.UTF8.GetString(line)).Append('\n');
                    var wi = new WordInfo(line, state.Strings);
                    if (!wi.IsInvalid) state.WordInfos.Add(wi);
                }
            }

            if (state.Error != null)
                throw new InvalidOperationException("Sudachi streaming callback error", state.Error);

            rawOutput = state.RawCapture?.ToString();

            var result = state.WordInfos;

            // Periodic recycle bounds native memory growth inside a long-lived context.
            if (++slot.CallCount % ContextRecycleThreshold == 0)
                RecycleContext(slot);

            return result;
        }
    }

    private static void RecycleContext(ContextSlot slot)
    {
        if (_freeContext != null)
        {
            foreach (var ctx in slot.Contexts.Values)
                if (ctx != IntPtr.Zero)
                    _freeContext(ctx);
        }
        slot.Contexts.Clear();
    }

    private static bool CsvUnchanged(byte[]? a, byte[]? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null || a.Length != b.Length) return false;
        return a.AsSpan().SequenceEqual(b);
    }

    private static IntPtr GetOrCreateContext(ContextSlot slot, string configPath, string dictionaryPath)
    {
        if (slot.Contexts.TryGetValue(configPath, out var existing) && existing != IntPtr.Zero)
            return existing;

        IntPtr errPtr;
        IntPtr ctx;

        if (slot.UserDictCsv is { Length: > 0 } && _createContextWithUserCsv != null)
        {
            var handle = GCHandle.Alloc(slot.UserDictCsv, GCHandleType.Pinned);
            try
            {
                errPtr = _createContextWithUserCsv(
                    configPath, dictionaryPath,
                    handle.AddrOfPinnedObject(), (nuint)slot.UserDictCsv.Length,
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

        slot.Contexts[configPath] = ctx;
        return ctx;
    }

    /// <summary>Call on application shutdown.</summary>
    public static void Cleanup()
    {
        while (_freeSlots.TryPop(out var slot))
            RecycleContext(slot);
    }

    private static unsafe void OnSudachiOutput(IntPtr userData, byte* data, nuint len)
    {
        var state = (CallbackState)GCHandle.FromIntPtr(userData).Target!;
        try
        {
            var span = new ReadOnlySpan<byte>(data, checked((int)len));
            int i = 0;

            while (true)
            {
                int nl = span.Slice(i).IndexOf((byte)'\n');
                if (nl < 0) break;

                ReadOnlySpan<byte> line = span.Slice(i, nl);

                if (state.LeftoverLen != 0)
                {
                    var tmp = new byte[state.LeftoverLen + line.Length];
                    Buffer.BlockCopy(state.Leftover, 0, tmp, 0, state.LeftoverLen);
                    line.CopyTo(tmp.AsSpan(state.LeftoverLen));
                    state.LeftoverLen = 0;
                    line = tmp;
                }

                if (line.Length != 0 && !line.SequenceEqual("EOS"u8))
                {
                    state.RawCapture?.Append(Encoding.UTF8.GetString(line)).Append('\n');
                    var wi = new WordInfo(line, state.Strings);
                    if (!wi.IsInvalid) state.WordInfos.Add(wi);
                }

                i += nl + 1;
            }

            // A partial line carries over to the next callback.
            var tail = span.Slice(i);
            if (!tail.IsEmpty)
            {
                if (state.Leftover.Length < state.LeftoverLen + tail.Length)
                    Array.Resize(ref state.Leftover, Math.Max(state.LeftoverLen + tail.Length, state.LeftoverLen * 2 + 1024));
                tail.CopyTo(state.Leftover.AsSpan(state.LeftoverLen));
                state.LeftoverLen += tail.Length;
            }
        }
        catch (Exception ex)
        {
            state.Error = ex;
        }
    }
}
