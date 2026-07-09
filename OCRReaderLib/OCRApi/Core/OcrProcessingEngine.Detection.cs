using OCRReaderLib.OCRApi.DTOs;

namespace OCRReaderLib.OCRApi.Core;

public partial class OcrProcessingEngine
{
    private static ImageExtractionMode DetectContentType(List<OcrTableCell> cells)
    {
        if (cells == null || cells.Count == 0)
            return ImageExtractionMode.PlainText;

        var valid = cells.Where(c => !string.IsNullOrWhiteSpace(c.Text)).ToList();

        if (valid.Count == 0)
            return ImageExtractionMode.PlainText;

        var rxcyCount = valid.Count(c => LooksLikeRxCy(c.Text));
        var rxcyRatio = (double)rxcyCount / valid.Count;

        if (rxcyRatio >= 0.50)
            return ImageExtractionMode.Table;

        var avgH = Math.Max(1, (int)valid.Average(c => c.Height));
        var rowTol = Math.Max(8, (int)(avgH * 0.75));

        var rowGroups = GroupByProximity(valid, c => c.Y + c.Height / 2.0, rowTol)
            .Where(r => r.Count >= 2)
            .ToList();

        if (rowGroups.Count >= 5)
        {
            var allX = valid.Select(c => c.X).OrderBy(x => x).ToList();
            var colTol = ComputeColumnTolerance(allX);
            var colCenters = ClusterValues(allX, colTol);

            if (colCenters.Count >= 3 && IsLikelyStructuredTableLayout(rowGroups, colCenters))
                return ImageExtractionMode.Table;
        }

        return ImageExtractionMode.PlainText;
    }

    private static bool IsLikelyStructuredTableLayout(List<List<OcrTableCell>> rowGroups, List<int> colCenters)
    {
        if (rowGroups.Count < 5 || colCenters.Count < 3)
            return false;

        var rowColumnSets = rowGroups
            .Select(row => row
                .Select(c => GetNearestIndex(c.X, colCenters))
                .Where(index => index >= 0)
                .Distinct()
                .OrderBy(index => index)
                .ToList())
            .Where(set => set.Count >= 2)
            .ToList();

        if (rowColumnSets.Count < Math.Max(3, (int)Math.Ceiling(rowGroups.Count * 0.6)))
            return false;

        var rowsWithAtLeastThreeColumns = rowColumnSets.Count(set => set.Count >= 3);
        if (rowsWithAtLeastThreeColumns < Math.Max(3, (int)Math.Ceiling(rowGroups.Count * 0.5)))
            return false;

        var minStableRows = Math.Max(3, (int)Math.Ceiling(rowGroups.Count * 0.6));
        var stableColumnCount = rowColumnSets
            .SelectMany(set => set)
            .GroupBy(index => index)
            .Count(group => group.Count() >= minStableRows);

        if (stableColumnCount < 3)
            return false;

        var widths = rowColumnSets.Select(set => set.Count).ToList();
        var avgWidth = widths.Average();
        var variance = widths.Select(width => Math.Pow(width - avgWidth, 2)).Average();
        var coefficientOfVariation = Math.Sqrt(variance) / Math.Max(1.0, avgWidth);

        return coefficientOfVariation <= 0.50;
    }

    private static bool LooksLikeRxCy(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 3)
            return false;

        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^R\d+C\d+$"))
            return true;

        if (text[0] == 'R' && text.Contains('C') && text.Length >= 3 && text.Any(char.IsDigit))
            return true;

        return false;
    }
}
