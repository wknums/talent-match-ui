using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace TalentMatch.Web.Client.Services;

public enum ApplicationExportFormat
{
    Csv,
    Xlsx
}

public sealed record ApplicationExportFile(string FileName, string ContentType, byte[] Content);

public static class ApplicationExportBuilder
{
    private static readonly string[] Headers =
    [
        "Application ID",
        "Candidate",
        "Email",
        "Status",
        "Score",
        "Variance",
        "Decision",
        "Submitted"
    ];

    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    public static ApplicationExportFile Create(
        IEnumerable<ApplicationDto> applications,
        string jobTitle,
        string viewName,
        ApplicationExportFormat format)
    {
        var rows = applications.ToList();
        var extension = format == ApplicationExportFormat.Csv ? "csv" : "xlsx";
        var fileName = BuildFileName(jobTitle, viewName, extension);

        return format switch
        {
            ApplicationExportFormat.Csv => new(
                fileName,
                "text/csv;charset=utf-8",
                CreateCsv(rows)),
            ApplicationExportFormat.Xlsx => new(
                fileName,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                CreateXlsx(rows, viewName)),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported application export format.")
        };
    }

    private static byte[] CreateCsv(IReadOnlyList<ApplicationDto> applications)
    {
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(',', Headers));

        foreach (var application in applications)
        {
            csv.AppendLine(string.Join(',', GetValues(application).Select(EscapeCsv)));
        }

        var content = Encoding.UTF8.GetBytes(csv.ToString());
        var preamble = Encoding.UTF8.GetPreamble();
        var result = new byte[preamble.Length + content.Length];
        preamble.CopyTo(result, 0);
        content.CopyTo(result, preamble.Length);
        return result;
    }

    private static byte[] CreateXlsx(IReadOnlyList<ApplicationDto> applications, string viewName)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteTextEntry(archive, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                </Types>
                """);
            WriteTextEntry(archive, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            WriteWorkbook(archive, viewName);
            WriteTextEntry(archive, "xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                </Relationships>
                """);
            WriteWorksheet(archive, applications);
        }

        return output.ToArray();
    }

    private static void WriteWorkbook(ZipArchive archive, string viewName)
    {
        using var writer = CreateXmlWriter(archive.CreateEntry("xl/workbook.xml").Open());
        writer.WriteStartDocument(true);
        writer.WriteStartElement("workbook", SpreadsheetNamespace);
        writer.WriteAttributeString("xmlns", "r", null, "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
        writer.WriteStartElement("sheets", SpreadsheetNamespace);
        writer.WriteStartElement("sheet", SpreadsheetNamespace);
        writer.WriteAttributeString("name", SanitizeWorksheetName(viewName));
        writer.WriteAttributeString("sheetId", "1");
        writer.WriteAttributeString("r", "id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships", "rId1");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteWorksheet(ZipArchive archive, IReadOnlyList<ApplicationDto> applications)
    {
        using var writer = CreateXmlWriter(archive.CreateEntry("xl/worksheets/sheet1.xml").Open());
        writer.WriteStartDocument(true);
        writer.WriteStartElement("worksheet", SpreadsheetNamespace);
        writer.WriteStartElement("sheetData", SpreadsheetNamespace);

        writer.WriteStartElement("row", SpreadsheetNamespace);
        writer.WriteAttributeString("r", "1");
        for (var column = 0; column < Headers.Length; column++)
        {
            WriteStringCell(writer, $"{ColumnName(column)}1", Headers[column]);
        }
        writer.WriteEndElement();

        for (var rowIndex = 0; rowIndex < applications.Count; rowIndex++)
        {
            var excelRow = rowIndex + 2;
            var application = applications[rowIndex];
            var values = GetValues(application);

            writer.WriteStartElement("row", SpreadsheetNamespace);
            writer.WriteAttributeString("r", excelRow.ToString(CultureInfo.InvariantCulture));
            for (var column = 0; column < values.Length; column++)
            {
                var reference = $"{ColumnName(column)}{excelRow}";
                if (column is 4 or 5)
                {
                    WriteNumberCell(writer, reference, values[column]);
                }
                else
                {
                    WriteStringCell(writer, reference, values[column]);
                }
            }
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteStartElement("autoFilter", SpreadsheetNamespace);
        writer.WriteAttributeString("ref", $"A1:H{Math.Max(applications.Count + 1, 1)}");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteStringCell(XmlWriter writer, string reference, string value)
    {
        writer.WriteStartElement("c", SpreadsheetNamespace);
        writer.WriteAttributeString("r", reference);
        writer.WriteAttributeString("t", "inlineStr");
        writer.WriteStartElement("is", SpreadsheetNamespace);
        writer.WriteStartElement("t", SpreadsheetNamespace);
        writer.WriteAttributeString("xml", "space", null, "preserve");
        writer.WriteString(value);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteNumberCell(XmlWriter writer, string reference, string value)
    {
        writer.WriteStartElement("c", SpreadsheetNamespace);
        writer.WriteAttributeString("r", reference);
        if (!string.IsNullOrEmpty(value))
        {
            writer.WriteElementString("v", SpreadsheetNamespace, value);
        }
        writer.WriteEndElement();
    }

    private static string[] GetValues(ApplicationDto application) =>
    [
        application.Id,
        string.IsNullOrWhiteSpace(application.CandidateName) ? application.CandidateRef : application.CandidateName,
        application.CandidateEmail ?? string.Empty,
        application.Status,
        application.FinalScore?.ToString("G17", CultureInfo.InvariantCulture) ?? string.Empty,
        application.Variance?.ToString("G17", CultureInfo.InvariantCulture) ?? string.Empty,
        application.FinalDecision ?? string.Empty,
        application.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
    ];

    private static string EscapeCsv(string value)
    {
        var safeValue = StartsWithFormulaOperator(value) ? $"'{value}" : value;
        return safeValue.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{safeValue.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : safeValue;
    }

    private static bool StartsWithFormulaOperator(string value)
    {
        var trimmed = value.TrimStart();
        return trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@';
    }

    private static string BuildFileName(string jobTitle, string viewName, string extension)
    {
        var source = $"{jobTitle}-{viewName}-applications".Normalize(NormalizationForm.FormD);
        var slug = new StringBuilder();
        var previousWasSeparator = false;

        foreach (var character in source)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsAsciiLetterOrDigit(character))
            {
                slug.Append(char.ToLowerInvariant(character));
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator && slug.Length > 0)
            {
                slug.Append('-');
                previousWasSeparator = true;
            }
        }

        var safeName = slug.ToString().Trim('-');
        return $"{(safeName.Length == 0 ? "applications" : safeName)}.{extension}";
    }

    private static string SanitizeWorksheetName(string value)
    {
        var sanitized = new string(value
            .Where(character => character is not '[' and not ']' and not ':' and not '*' and not '?' and not '/' and not '\\')
            .ToArray())
            .Trim();
        var safeName = sanitized.Length == 0 ? "Applications" : sanitized;
        return safeName[..Math.Min(safeName.Length, 31)];
    }

    private static string ColumnName(int index)
    {
        var value = index + 1;
        var name = string.Empty;
        while (value > 0)
        {
            var remainder = (value - 1) % 26;
            name = (char)('A' + remainder) + name;
            value = (value - 1) / 26;
        }

        return name;
    }

    private static XmlWriter CreateXmlWriter(Stream stream) =>
        XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            CloseOutput = true,
            Indent = false
        });

    private static void WriteTextEntry(ZipArchive archive, string path, string content)
    {
        using var writer = new StreamWriter(
            archive.CreateEntry(path).Open(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }
}
