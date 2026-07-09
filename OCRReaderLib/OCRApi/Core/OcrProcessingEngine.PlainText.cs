using OCRReaderLib.OCRApi.DTOs;
using System.Linq;
using System.Text.RegularExpressions;
using Windows.Media.Ocr;

namespace OCRReaderLib.OCRApi.Core;

public partial class OcrProcessingEngine
{
    private static string BuildPlainText(OcrResult ocrResult, List<OcrTableCell> rawCells)
    {
        var lines = new List<string>();

        foreach (var line in ocrResult.Lines)
        {
            var words = line.Words
                .Select(w => CorrectOcrPatterns(w.Text.Trim()))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            if (words.Count > 0)
                lines.Add(string.Join(" ", words));
        }

        if (TryBuildParameterExportTableFallback(rawCells, new OcrTableResult(), out var tableFallback))
        {
            var rowCount = tableFallback.Cells.Select(c => c.Row).Distinct().Count();
            if (rowCount >= 8)
            {
                var tableText = tableFallback.ToString();
                if (!string.IsNullOrWhiteSpace(tableText))
                {
                    var headingLines = ExtractHeadingLinesAboveParameterTable(ocrResult, rawCells);
                    if (headingLines.Count > 0)
                        return string.Join(Environment.NewLine, headingLines) + Environment.NewLine + Environment.NewLine + tableText.TrimEnd();

                    return tableText.TrimEnd();
                }
            }
        }

        if (TryBuildInferredPercentAxis(rawCells, out var inferredAxis))
        {
            var nonPercentTokens = rawCells
                .Select(c => c.Text?.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t) && !TryParsePercent(t!, out _))
                .Distinct()
                .ToList();

            var merged = inferredAxis.Concat(nonPercentTokens).ToList();
            if (merged.Count > lines.Count)
                return string.Join(Environment.NewLine, merged);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static List<string> ExtractHeadingLinesAboveParameterTable(OcrResult ocrResult, List<OcrTableCell> rawCells)
    {
        var headings = new List<string>();

        if (ocrResult?.Lines == null || rawCells == null || rawCells.Count == 0)
            return headings;

        var parameterRows = rawCells
            .Where(c => LooksLikeParameterId(c.Text))
            .OrderBy(c => c.Y)
            .ToList();

        if (parameterRows.Count == 0)
            return headings;

        var firstParameterY = parameterRows[0].Y;
        var avgParamHeight = Math.Max(1, (int)parameterRows.Take(8).Average(c => Math.Max(1, c.Height)));
        var headingLimitY = firstParameterY - Math.Max(6, avgParamHeight / 2);

        if (headingLimitY <= 0)
            return headings;

        var orderedLines = ocrResult.Lines
            .Select(line => new
            {
                Line = line,
                TopY = line.Words.Count > 0 ? line.Words.Min(w => w.BoundingRect.Y) : double.MaxValue,
                BottomY = line.Words.Count > 0 ? line.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height) : double.MaxValue
            })
            .OrderBy(x => x.TopY)
            .ToList();

        foreach (var item in orderedLines)
        {
            if (item.BottomY > headingLimitY)
                continue;

            var text = string.Join(" ", item.Line.Words
                .Select(w => CorrectOcrPatterns(w.Text.Trim()))
                .Where(t => !string.IsNullOrWhiteSpace(t)));

            if (string.IsNullOrWhiteSpace(text))
                continue;

            headings.Add(text);
        }

        return headings.Distinct().ToList();
    }

    private static string ExtractSurroundingTextOutsideTable(OcrResult ocrResult, OcrTableResult tableResult)
    {
        if (ocrResult?.Lines == null || ocrResult.Lines.Count == 0)
            return string.Empty;

        var spatialCells = tableResult.Cells
            .Concat(tableResult.ColumnHeaders)
            .Concat(tableResult.RowHeaders)
            .Where(c => c.Width > 1 && c.Height > 1)
            .ToList();

        if (spatialCells.Count == 0)
            return string.Empty;

        // Surrounding text is anything clearly above the table (title/description) or
        // below it. Anything within the table's vertical span is table content
        // (header row or data/value cells) and must not be duplicated here.
        var tableTop = spatialCells.Min(c => c.Y);
        var tableBottom = spatialCells.Max(c => c.Y + c.Height);
        var avgHeight = Math.Max(1, (int)spatialCells.Average(c => c.Height));
        var margin = Math.Max(6, avgHeight / 2);

        var lines = new List<string>();

        foreach (var line in ocrResult.Lines)
        {
            if (line.Words.Count == 0)
                continue;

            var lineTop = line.Words.Min(w => w.BoundingRect.Y);
            var lineBottom = line.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
            var lineCenterY = (lineTop + lineBottom) / 2.0;

            var isAboveTable = lineCenterY < tableTop - margin;
            var isBelowTable = lineCenterY > tableBottom + margin;

            if (!isAboveTable && !isBelowTable)
                continue;

            var tokens = line.Words
                .Select(w => CorrectOcrPatterns(w.Text.Trim()))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            if (tokens.Count == 0)
                continue;

            // Drop stray single glyphs (e.g. a close "x") and value-only fragments
            // that are really detached table cells.
            if (tokens.Count == 1 && tokens[0].Length == 1)
                continue;

            if (tokens.Count <= 2 && tokens.All(LooksLikeValueCell))
                continue;

            lines.Add(string.Join(" ", tokens));
        }

        return string.Join(Environment.NewLine, lines.Distinct());
    }

    private static bool TryBuildInferredPercentAxis(List<OcrTableCell> cells, out List<string> inferredAxis)
    {
        inferredAxis = new List<string>();

        if (cells == null || cells.Count == 0)
            return false;

        var percentCells = cells
            .Select(c => new { Cell = c, Value = TryParsePercent(c.Text, out var value) ? value : -1 })
            .Where(x => x.Value >= 0)
            .ToList();

        if (percentCells.Count < 2)
            return false;

        var xSpread = percentCells.Max(x => x.Cell.X) - percentCells.Min(x => x.Cell.X);
        var avgWidth = Math.Max(1.0, percentCells.Average(x => x.Cell.Width));

        if (xSpread > avgWidth * 3.5)
            return false;

        var distinctValues = percentCells
            .Select(x => x.Value)
            .Distinct()
            .OrderByDescending(v => v)
            .ToList();

        if (distinctValues.Count < 2)
            return false;

        var max = distinctValues.First();
        var min = distinctValues.Last();

        if (max < 70 || min > 50)
            return false;

        var normalizedTop = max >= 90 ? 100 : max;

        var normalizedBottom = min <= 10
            ? 0
            : normalizedTop == 100
                ? 0
                : min;

        if (normalizedTop <= normalizedBottom)
            return false;

        for (var value = normalizedTop; value >= normalizedBottom; value -= 10)
            inferredAxis.Add($"{value}%");

        return inferredAxis.Count > distinctValues.Count;
    }

    private static bool TryParsePercent(string text, out int percent)
    {
        percent = -1;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var match = Regex.Match(text.Trim(), @"^(\d{1,3})\s*%$");
        if (!match.Success)
            return false;

        if (!int.TryParse(match.Groups[1].Value, out percent))
            return false;

        return percent >= 0 && percent <= 100;
    }
}
