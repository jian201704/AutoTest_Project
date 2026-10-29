using System.IO.Compression;
using System.Xml.Linq;

namespace Tap.Plugins.Workflow;

public sealed class OpenXmlTable
{
    private static readonly XNamespace Spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";

    private OpenXmlTable(List<IReadOnlyDictionary<string, string>> rows) => Rows = rows;

    public IReadOnlyList<IReadOnlyDictionary<string, string>> Rows { get; }

    public static OpenXmlTable Load(string path, string sheetName, params string[] requiredHeaders)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Excel workbook was not found.", path);
        using var archive = ZipFile.OpenRead(path);
        var workbook = LoadXml(archive, "xl/workbook.xml");
        var relationships = LoadXml(archive, "xl/_rels/workbook.xml.rels");
        var sheet = workbook.Root?.Element(Spreadsheet + "sheets")?.Elements(Spreadsheet + "sheet")
            .FirstOrDefault(item => string.Equals((string?)item.Attribute("name"), sheetName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Worksheet '{sheetName}' was not found.");
        var relationshipId = (string?)sheet.Attribute(Relationships + "id")
            ?? throw new InvalidOperationException($"Worksheet '{sheetName}' has no relationship.");
        var relationship = relationships.Root?.Elements(PackageRelationships + "Relationship")
            .FirstOrDefault(item => string.Equals((string?)item.Attribute("Id"), relationshipId, StringComparison.Ordinal));
        var target = (string?)relationship?.Attribute("Target")
            ?? throw new InvalidOperationException($"Worksheet '{sheetName}' relationship target was not found.");
        var worksheet = LoadXml(archive, target.StartsWith("/", StringComparison.Ordinal) ? target.TrimStart('/') : "xl/" + target.TrimStart('/'));
        var sharedStrings = LoadSharedStrings(archive);
        var headers = new Dictionary<int, string>();
        var result = new List<IReadOnlyDictionary<string, string>>();

        foreach (var row in worksheet.Root?.Element(Spreadsheet + "sheetData")?.Elements(Spreadsheet + "row") ?? Enumerable.Empty<XElement>())
        {
            var values = ReadCells(row, sharedStrings);
            if (headers.Count == 0)
            {
                foreach (var pair in values)
                    if (!string.IsNullOrWhiteSpace(pair.Value)) headers[pair.Key] = pair.Value.Trim();
                continue;
            }
            result.Add(headers.ToDictionary(pair => pair.Value,
                pair => values.TryGetValue(pair.Key, out var value) ? value : "", StringComparer.OrdinalIgnoreCase));
        }
        var missingHeaders = requiredHeaders.Where(required => !headers.Values.Contains(required, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (missingHeaders.Length != 0)
            throw new InvalidDataException($"Worksheet '{sheetName}' is missing required columns: {string.Join(", ", missingHeaders)}.");
        return new OpenXmlTable(result);
    }

    private static Dictionary<int, string> ReadCells(XElement row, IReadOnlyList<string> sharedStrings)
    {
        var values = new Dictionary<int, string>();
        foreach (var cell in row.Elements(Spreadsheet + "c"))
        {
            var column = 0;
            foreach (var character in ((string?)cell.Attribute("r") ?? "").TakeWhile(char.IsLetter))
                column = column * 26 + char.ToUpperInvariant(character) - 'A' + 1;
            var type = (string?)cell.Attribute("t");
            var value = type == "inlineStr" ? cell.Element(Spreadsheet + "is")?.Value ?? "" : cell.Element(Spreadsheet + "v")?.Value ?? "";
            if (type == "s" && int.TryParse(value, out var index) && index >= 0 && index < sharedStrings.Count)
                value = sharedStrings[index];
            values[column] = value;
        }
        return values;
    }

    private static List<string> LoadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry == null) return new List<string>();
        using var stream = entry.Open();
        return XDocument.Load(stream).Root?.Elements(Spreadsheet + "si").Select(item => item.Value).ToList() ?? new List<string>();
    }

    private static XDocument LoadXml(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path.Replace('\\', '/')) ?? throw new InvalidDataException($"Excel entry '{path}' was not found.");
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }
}