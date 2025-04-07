using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jiten.Cli;

public class MokuroExtractor
{
    public async Task<string> Extract(string? filePath, bool verbose)
    {
        string?[] files = [];

        // TODO: Handle subfolders separately
        if (Directory.Exists(filePath))
        {
            files = Directory.GetFiles(filePath, "*.json", SearchOption.AllDirectories);
        }
        else
        {
            files = new string[] { filePath };
        }

        if (verbose)
        {
            Console.WriteLine($"Found {files.Length} files to extract.");

            foreach (var file in files)
            {
                Console.WriteLine(file);
            }
        }

        StringBuilder extractedText = new();

        foreach (var file in files)
        {
            if (file == null) continue;

            if (verbose)
            {
                Console.WriteLine($"Processing file: {file}");
            }

            try
            {
                var json = JsonDocument.Parse(await File.ReadAllTextAsync(file));
                var blocks = json.RootElement.GetProperty("blocks");

                foreach (JsonElement block in blocks.EnumerateArray())
                {
                    var lines = block.GetProperty("lines");
                    foreach (JsonElement line in lines.EnumerateArray())
                    {
                        // Remove publisher info etc
                        var cleanedLine = Regex.Replace(line.GetString() ?? string.Empty, "^[０-９Ａ-Ｚａ-ｚ－一．第話第章：]*$", "");
                        // Remove excessive punctuation only lnies
                        cleanedLine = Regex.Replace(cleanedLine ?? string.Empty, "^[．！？]*$", "");
                        cleanedLine = Regex.Replace(cleanedLine ?? string.Empty, "．．．．*", "．．．");
                        // Remove single character followed by punctuation or not
                        cleanedLine = Regex.Replace(cleanedLine ?? string.Empty, @"^[\u3040-\u309F\u30A0-\u30FF][．！？]*$", "");
                        // Remove romaji string longer than 4 characters, very high change they're badly OCR'd
                        cleanedLine = Regex.Replace(cleanedLine ?? string.Empty, @"[Ａ-Ｚａ-ｚ：／．]{3,}", "");

                        cleanedLine = cleanedLine.Trim();

                        if (string.IsNullOrWhiteSpace(cleanedLine)) continue;
                        extractedText.Append(cleanedLine);
                    }

                    extractedText.AppendLine();
                }
            }
            catch (Exception ex)
            {
                if (verbose)
                {
                    Console.WriteLine($"Error processing file {file}: {ex.Message}");
                }
            }
        }

        return extractedText.ToString();
    }
}