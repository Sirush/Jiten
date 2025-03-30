using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Jiten.Cli;

/// <summary>
/// Extract KiriKiri's PSB scenario files
/// </summary>
public class PsbExtractor
{
    public class Root
    {
        [JsonPropertyName("scenes")] public List<Scene> Scenes { get; set; }
    }

    public class Scene
    {
        [JsonPropertyName("texts")] public List<List<object>> Texts { get; set; }
    }

    public async Task<string> Extract(string? filePath, bool verbose)
    {
        string?[] files = [];

        // TODO: Handle subfolders separately
        if (Directory.Exists(filePath))
        {
            files = Directory.GetFiles(filePath, "*.txt.json", SearchOption.AllDirectories)
                             .Concat(Directory.GetFiles(filePath, "*.ks.json", SearchOption.AllDirectories))
                             .ToArray();
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
            var jsonContent = await File.ReadAllTextAsync(file, Encoding.UTF8);

            var rootObject = JsonSerializer.Deserialize<Root>(jsonContent);

            // Process the rootObject as needed
            foreach (var scene in rootObject.Scenes)
            {
                if (scene.Texts == null) continue;

                foreach (var text in scene.Texts)
                {
                    if (text.Count <= 2 || text[2] == null) continue;

                    // Filter [tags]
                    string line = Regex.Replace(text[2].ToString(), @"\[.*?\]", "", RegexOptions.None);
                    line = Regex.Replace(line, @"\\n", "", RegexOptions.None);

                    extractedText.AppendLine(line);
                }
            }
        }

        return extractedText.ToString();
    }
}