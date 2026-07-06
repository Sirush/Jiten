using System.Reflection;
using System.Text;
using Jiten.Core;
using Jiten.Parser.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Cli;

/// <summary>
/// Runs parser segmentation tests with full diagnostics for debugging and fix suggestions.
/// Uses full Parser.ParseText path (same as unit tests) when context factory is provided.
/// </summary>
public class DiagnosticTestRunner
{
    private readonly IDbContextFactory<JitenDbContext>? _contextFactory;

    public DiagnosticTestRunner(IDbContextFactory<JitenDbContext>? contextFactory = null)
    {
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// Loads test cases by reflecting over MemberData attributes in the test assembly
    /// </summary>
    public List<(string Input, string[] Expected)> LoadTestCasesFromAssembly()
    {
        var testCases = new List<(string, string[])>();

        // Try multiple paths to find the test assembly
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidatePaths = new[]
        {
            Path.Combine(baseDir, "Jiten.Tests.dll"),
            Path.Combine(baseDir, "..", "..", "..", "..", "Jiten.Tests", "bin", "Debug", "net9.0", "Jiten.Tests.dll"),
            Path.Combine(baseDir, "..", "..", "..", "..", "Jiten.Tests", "bin", "Release", "net9.0", "Jiten.Tests.dll"),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "Jiten.Tests", "bin", "Debug", "net9.0", "Jiten.Tests.dll")),
        };

        string? testAssemblyPath = null;
        foreach (var path in candidatePaths)
        {
            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
            {
                testAssemblyPath = fullPath;
                break;
            }
        }

        if (testAssemblyPath == null)
        {
            Console.WriteLine("Error: Test assembly not found. Searched paths:");
            foreach (var path in candidatePaths)
            {
                Console.WriteLine($"  - {Path.GetFullPath(path)}");
            }
            return testCases;
        }

        try
        {
            var testAssembly = Assembly.LoadFrom(testAssemblyPath);
            var testClass = testAssembly.GetType("Jiten.Tests.MorphologicalAnalyserTests");
            if (testClass == null)
            {
                Console.WriteLine("Error: Test class 'Jiten.Tests.MorphologicalAnalyserTests' not found in assembly.");
                return testCases;
            }

            var testMethod = testClass.GetMethod("SegmentationTest");
            if (testMethod == null)
            {
                Console.WriteLine("Error: Test method 'SegmentationTest' not found in class.");
                return testCases;
            }

            // Look for MemberDataAttribute on the test method
            var attributes = testMethod.GetCustomAttributes(true);
            foreach (var attr in attributes)
            {
                var attrType = attr.GetType();
                if (attrType.Name == "MemberDataAttribute")
                {
                    // Get the MemberName property to find the data source method
                    var memberNameProp = attrType.GetProperty("MemberName");
                    if (memberNameProp == null) continue;

                    var memberName = memberNameProp.GetValue(attr) as string;
                    if (string.IsNullOrEmpty(memberName)) continue;

                    // Find the method that provides the test data
                    var dataMethod = testClass.GetMethod(memberName, BindingFlags.Public | BindingFlags.Static);
                    if (dataMethod == null)
                    {
                        Console.WriteLine($"Error: MemberData source method '{memberName}' not found.");
                        continue;
                    }

                    // Invoke the method to get test data
                    var result = dataMethod.Invoke(null, null);
                    if (result is IEnumerable<object[]> dataEnumerable)
                    {
                        foreach (var data in dataEnumerable)
                        {
                            if (data?.Length >= 2 && data[0] is string input && data[1] is string[] expected)
                            {
                                testCases.Add((input, expected));
                            }
                        }
                    }
                }
            }

            if (testCases.Count == 0)
            {
                Console.WriteLine("Warning: No test cases found. Check that MemberData attribute and source method are correct.");
            }
            else
            {
                Console.WriteLine($"Loaded {testCases.Count} test cases from {Path.GetFileName(testAssemblyPath)}");
            }

            return testCases;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading test assembly: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }

        return testCases;
    }

    public async Task<FormTestRunResult> RunFormSelectionTests()
    {
        var testCases = LoadFormSelectionCasesFromAssembly();
        var failures = new List<FormTestFailure>();
        var passed = 0;

        Console.WriteLine($"Running {testCases.Count} form selection tests...");

        foreach (var (input, expectedToken, expectedWordId, expectedReadingIndex) in testCases)
        {
            if (_contextFactory == null)
            {
                Console.WriteLine($"Test failed: {input} - No context factory available");
                continue;
            }

            var result = await Parser.Parser.ParseText(_contextFactory, input);
            var match = result.FirstOrDefault(w => w.OriginalText == expectedToken);

            if (match == null)
            {
                failures.Add(new FormTestFailure
                {
                    Input = input, ExpectedToken = expectedToken,
                    ExpectedWordId = expectedWordId, ExpectedReadingIndex = expectedReadingIndex,
                    Reason = $"Token '{expectedToken}' not found in parse results"
                });
                continue;
            }

            if (match.WordId == expectedWordId && match.ReadingIndex == expectedReadingIndex)
            {
                passed++;
                continue;
            }

            var reasons = new List<string>();
            if (match.WordId != expectedWordId)
                reasons.Add($"WordId: expected {expectedWordId}, got {match.WordId}");
            if (match.ReadingIndex != expectedReadingIndex)
                reasons.Add($"ReadingIndex: expected {expectedReadingIndex}, got {match.ReadingIndex}");

            failures.Add(new FormTestFailure
            {
                Input = input, ExpectedToken = expectedToken,
                ExpectedWordId = expectedWordId, ExpectedReadingIndex = expectedReadingIndex,
                ActualWordId = match.WordId, ActualReadingIndex = match.ReadingIndex,
                Reason = string.Join("; ", reasons)
            });
        }

        return new FormTestRunResult
        {
            TotalTests = testCases.Count, Passed = passed, Failed = failures.Count, Failures = failures
        };
    }

    public List<(string Input, string ExpectedToken, int ExpectedWordId, byte ExpectedReadingIndex)> LoadFormSelectionCasesFromAssembly()
    {
        var testCases = new List<(string, string, int, byte)>();

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidatePaths = new[]
        {
            Path.Combine(baseDir, "Jiten.Tests.dll"),
            Path.Combine(baseDir, "..", "..", "..", "..", "Jiten.Tests", "bin", "Debug", "net9.0", "Jiten.Tests.dll"),
            Path.Combine(baseDir, "..", "..", "..", "..", "Jiten.Tests", "bin", "Release", "net9.0", "Jiten.Tests.dll"),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "Jiten.Tests", "bin", "Debug", "net9.0", "Jiten.Tests.dll")),
        };

        string? testAssemblyPath = null;
        foreach (var path in candidatePaths)
        {
            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
            {
                testAssemblyPath = fullPath;
                break;
            }
        }

        if (testAssemblyPath == null)
        {
            Console.WriteLine("Error: Test assembly not found. Searched paths:");
            foreach (var path in candidatePaths)
                Console.WriteLine($"  - {Path.GetFullPath(path)}");
            return testCases;
        }

        try
        {
            var testAssembly = Assembly.LoadFrom(testAssemblyPath);
            var testClass = testAssembly.GetType("Jiten.Tests.FormSelectionTests");
            if (testClass == null)
            {
                Console.WriteLine("Error: Test class 'Jiten.Tests.FormSelectionTests' not found in assembly.");
                return testCases;
            }

            var dataMethod = testClass.GetMethod("FormSelectionCases", BindingFlags.Public | BindingFlags.Static);
            if (dataMethod == null)
            {
                Console.WriteLine("Error: Method 'FormSelectionCases' not found in class.");
                return testCases;
            }

            var result = dataMethod.Invoke(null, null);
            if (result is IEnumerable<object[]> dataEnumerable)
            {
                foreach (var data in dataEnumerable)
                {
                    if (data?.Length >= 4 && data[0] is string input && data[1] is string expectedToken
                                          && data[2] is int expectedWordId && data[3] is byte expectedReadingIndex)
                    {
                        testCases.Add((input, expectedToken, expectedWordId, expectedReadingIndex));
                    }
                }
            }

            if (testCases.Count == 0)
                Console.WriteLine("Warning: No form selection test cases found.");
            else
                Console.WriteLine($"Loaded {testCases.Count} form selection test cases from {Path.GetFileName(testAssemblyPath)}");

            return testCases;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading test assembly: {ex.Message}");
        }

        return testCases;
    }

    public async Task<TestRunResult> RunSegmentationTests()
    {
        var testCases = LoadTestCasesFromAssembly();
        var failures = new List<TestFailure>();
        var passed = 0;

        Console.WriteLine($"Running {testCases.Count} tests...");

        foreach (var (input, expected) in testCases)
        {
            var diagnostics = new ParserDiagnostics { InputText = input };
            string[] actual;

            if (_contextFactory != null)
            {
                // Use full Parser.ParseText path (same as unit tests)
                var result = await Parser.Parser.ParseText(_contextFactory, input, diagnostics: diagnostics);
                actual = result.Select(w => w.OriginalText).ToArray();
            }
            else
            {
                // Failure
                Console.WriteLine($"Test failed: {input} - No context factory available");
                continue;
            }

            if (actual.SequenceEqual(expected))
            {
                passed++;
                continue;
            }

            failures.Add(new TestFailure
                         {
                             Input = input, Expected = expected, Actual = actual, Diagnostics = diagnostics,
                             Analysis = AnalyseFailure(expected, actual, diagnostics)
                         });
        }

        return new TestRunResult { TotalTests = testCases.Count, Passed = passed, Failed = failures.Count, Failures = failures };
    }

    private FailureAnalysis AnalyseFailure(string[] expected, string[] actual, ParserDiagnostics diagnostics)
    {
        var analysis = new FailureAnalysis();

        // Determine failure type
        if (actual.Length > expected.Length)
        {
            analysis.Type = "OverSegmentation";
            analysis.Description = "Parser split tokens that should remain combined";
            analysis.ProbableCause = IdentifyOverSegmentationCause(expected, actual, diagnostics);
            analysis.SuggestedFix = SuggestCombineFix(expected, actual, diagnostics);
        }
        else if (actual.Length < expected.Length)
        {
            analysis.Type = "UnderSegmentation";
            analysis.Description = "Parser combined tokens that should be separate";
            analysis.ProbableCause = IdentifyUnderSegmentationCause(expected, actual, diagnostics);
            analysis.SuggestedFix = SuggestSplitFix(expected, actual, diagnostics);
        }
        else
        {
            analysis.Type = "TokenMismatch";
            analysis.Description = "Token count matches but content differs";
            analysis.ProbableCause = IdentifyMismatchCause(expected, actual, diagnostics);
            analysis.SuggestedFix = SuggestMismatchFix(expected, actual, diagnostics);
        }

        return analysis;
    }

    private string? IdentifyOverSegmentationCause(string[] expected, string[] actual, ParserDiagnostics diagnostics)
    {
        // Check if Sudachi already split the tokens
        if (diagnostics.Sudachi?.Tokens != null)
        {
            var sudachiTokens = diagnostics.Sudachi.Tokens.Select(t => t.Surface).ToList();

            // Find which expected token was split
            foreach (var expectedToken in expected)
            {
                var matchingActual = new List<string>();
                var remaining = expectedToken;

                foreach (var actualToken in actual)
                {
                    if (remaining.StartsWith(actualToken))
                    {
                        matchingActual.Add(actualToken);
                        remaining = remaining[actualToken.Length..];
                        if (remaining.Length == 0) break;
                    }
                }

                if (matchingActual.Count > 1)
                {
                    // Check if this was in Sudachi output
                    if (sudachiTokens.SequenceEqual(actual.Take(sudachiTokens.Count)))
                    {
                        return $"Sudachi split '{expectedToken}' into [{string.Join(", ", matchingActual)}]";
                    }
                }
            }
        }

        // Check processing stages
        foreach (var stage in diagnostics.TokenStages)
        {
            if (stage.InputTokenCount < stage.OutputTokenCount)
            {
                return $"Stage '{stage.StageName}' split tokens";
            }
        }

        return "Unknown - examine Sudachi output and processing stages";
    }

    private string? IdentifyUnderSegmentationCause(string[] expected, string[] actual, ParserDiagnostics diagnostics)
    {
        // Check which stage combined the tokens
        foreach (var stage in diagnostics.TokenStages)
        {
            if (stage.Modifications.Any(m => m.Type == "merge"))
            {
                var merges = stage.Modifications.Where(m => m.Type == "merge").ToList();
                foreach (var merge in merges)
                {
                    return $"Stage '{stage.StageName}' merged [{string.Join(", ", merge.InputTokens)}] → '{merge.OutputTokens.FirstOrDefault()}'";
                }
            }
        }

        return "Unknown - examine processing stages for unexpected merges";
    }

    private string? IdentifyMismatchCause(string[] expected, string[] actual, ParserDiagnostics diagnostics)
    {
        // Find first differing token
        for (int i = 0; i < Math.Min(expected.Length, actual.Length); i++)
        {
            if (expected[i] != actual[i])
            {
                return $"Token at index {i}: expected '{expected[i]}' but got '{actual[i]}'";
            }
        }

        return "Unknown mismatch";
    }

    private string SuggestCombineFix(string[] expected, string[] actual, ParserDiagnostics diagnostics)
    {
        var sb = new StringBuilder();

        // Find which tokens need combining
        int actualIdx = 0;
        foreach (var expectedToken in expected)
        {
            if (actualIdx >= actual.Length) break;

            if (actual[actualIdx] == expectedToken)
            {
                actualIdx++;
                continue;
            }

            // Check if consecutive actual tokens should be combined
            var combined = new StringBuilder();
            var tokensToMerge = new List<string>();
            var startIdx = actualIdx;

            while (actualIdx < actual.Length && combined.Length < expectedToken.Length)
            {
                combined.Append(actual[actualIdx]);
                tokensToMerge.Add(actual[actualIdx]);
                actualIdx++;
            }

            if (combined.ToString() == expectedToken && tokensToMerge.Count > 1)
            {
                if (tokensToMerge.Count == 2)
                {
                    sb.AppendLine($"// Add to MorphologicalAnalyser.SpecialCases2:");
                    sb.AppendLine($"(\"{tokensToMerge[0]}\", \"{tokensToMerge[1]}\", PartOfSpeech.Expression),");
                }
                else if (tokensToMerge.Count == 3)
                {
                    sb.AppendLine($"// Add to MorphologicalAnalyser.SpecialCases3:");
                    sb.AppendLine($"(\"{tokensToMerge[0]}\", \"{tokensToMerge[1]}\", \"{tokensToMerge[2]}\", PartOfSpeech.Expression),");
                }
                else
                {
                    sb.AppendLine($"// Need custom logic to combine: [{string.Join(", ", tokensToMerge)}] → '{expectedToken}'");
                }
            }
        }

        return sb.Length > 0 ? sb.ToString() : "No specific fix suggested - examine diagnostics";
    }

    private string SuggestSplitFix(string[] expected, string[] actual, ParserDiagnostics diagnostics)
    {
        var sb = new StringBuilder();

        // Find which actual token contains multiple expected tokens
        int expectedIdx = 0;
        foreach (var actualToken in actual)
        {
            if (expectedIdx >= expected.Length) break;

            if (actualToken == expected[expectedIdx])
            {
                expectedIdx++;
                continue;
            }

            // Check if actual token contains multiple expected tokens
            var remaining = actualToken;
            var expectedParts = new List<string>();

            while (expectedIdx < expected.Length && remaining.StartsWith(expected[expectedIdx]))
            {
                expectedParts.Add(expected[expectedIdx]);
                remaining = remaining[expected[expectedIdx].Length..];
                expectedIdx++;
            }

            if (expectedParts.Count > 1 && remaining.Length == 0)
            {
                sb.AppendLine($"// Actual '{actualToken}' should be split into: [{string.Join(", ", expectedParts)}]");
                sb.AppendLine($"// Check which Combine* method merged these tokens incorrectly");

                // Check if it was a specific stage
                foreach (var stage in diagnostics.TokenStages)
                {
                    var merge = stage.Modifications.FirstOrDefault(m =>
                                                                       m.Type == "merge" && m.OutputTokens is [var mergedToken] && mergedToken == actualToken);
                    if (merge != null)
                    {
                        sb.AppendLine($"// Caused by stage: {stage.StageName}");
                        break;
                    }
                }
            }
        }

        return sb.Length > 0 ? sb.ToString() : "No specific fix suggested - examine diagnostics";
    }

    private string SuggestMismatchFix(string[] expected, string[] actual, ParserDiagnostics diagnostics)
    {
        var sb = new StringBuilder();

        for (int i = 0; i < Math.Min(expected.Length, actual.Length); i++)
        {
            if (expected[i] != actual[i])
            {
                sb.AppendLine($"// Token {i}: '{actual[i]}' should be '{expected[i]}'");

                // Check Sudachi output
                var sudachiToken = diagnostics.Sudachi?.Tokens.FirstOrDefault(t => t.Surface == actual[i]);
                if (sudachiToken != null)
                {
                    sb.AppendLine($"// Sudachi parsed as: {sudachiToken.PartOfSpeech} (dict: {sudachiToken.DictionaryForm})");
                }
            }
        }

        return sb.Length > 0 ? sb.ToString() : "No specific fix suggested";
    }
}