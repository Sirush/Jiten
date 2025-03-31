namespace Jiten.Cli;

using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Simply combine multiple text file, no preprocessing
/// </summary>
public class TxtExtractor
{
    public async Task<string> Extract(string? filePath, string encoding, bool verbose)
    {
        string?[] files = [];

        // TODO: Handle subfolders separately
        if (Directory.Exists(filePath))
        {
            files = Directory.GetFiles(filePath, "*.*", SearchOption.AllDirectories);
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
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        foreach (var file in files)
        {
            var lines = await File.ReadAllLinesAsync(file, Encoding.GetEncoding(encoding));

            foreach (var line in lines)
            {
                string trimmedLine = line.Trim();

                if (!string.IsNullOrWhiteSpace(trimmedLine))
                    extractedText.AppendLine(trimmedLine);
            }
        }

        return extractedText.ToString();
    }
}