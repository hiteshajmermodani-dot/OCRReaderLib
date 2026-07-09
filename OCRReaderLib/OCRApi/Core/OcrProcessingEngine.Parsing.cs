using OCRReaderLib.OCRApi.DTOs;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Media.Ocr;

namespace OCRReaderLib.OCRApi.Core;

public partial class OcrProcessingEngine
{
    private static List<OcrTableCell> ParseOcrResultToCells(OcrResult ocrResult, int imageWidth, int imageHeight)
    {
        var allCells = new List<OcrTableCell>();

        foreach (var line in ocrResult.Lines)
        foreach (var word in line.Words)
        {
            var rect = word.BoundingRect;
            var text = word.Text?.Trim();

            if (string.IsNullOrWhiteSpace(text))
                continue;

            allCells.Add(new OcrTableCell
            {
                Text = text,
                X = (int)rect.X,
                Y = (int)rect.Y,
                Width = (int)rect.Width,
                Height = (int)rect.Height,
                Row = 0,
                Column = 0
            });
        }

        if (allCells.Count == 0)
            return allCells;

        var avgH = Math.Max(1, (int)allCells.Average(c => c.Height));
        var medianH = allCells.Select(c => c.Height).OrderBy(h => h).ElementAt(allCells.Count / 2);
        var rowTol = Math.Clamp((int)(medianH * 0.55), 8, 24);

        var rowGroups = GroupByProximity(allCells, c => c.Y + c.Height / 2.0, rowTol)
            .OrderBy(row => row.Min(c => c.Y))
            .ToList();

        for (var rowIdx = 0; rowIdx < rowGroups.Count; rowIdx++)
            foreach (var cell in rowGroups[rowIdx])
                cell.Row = rowIdx;

        var rowHeaderCells = allCells
            .Where(c => c.X <= RowNumMaxXBase && IsPotentialRowHeaderToken(c.Text))
            .Select(CloneCell)
            .ToList();

        var tableCells = allCells
            .Where(c => !(c.X <= RowNumMaxXBase && IsPotentialRowHeaderToken(c.Text)))
            .Select(CloneCell)
            .ToList();

        if (tableCells.Count == 0)
            return ConsolidateRowHeaders(rowHeaderCells);

        var tableRowGroups = GroupByProximity(tableCells, c => c.Y + c.Height / 2.0, rowTol)
            .OrderBy(row => row.Min(c => c.Y))
            .ToList();

        tableCells = MergeAdjacentCellsHorizontally(tableCells, tableRowGroups, avgH);

        // Filter out stray noise (single characters, bullets, etc.) that throw off column clustering.
        // These are typically OCR artifacts or image noise that create phantom X-positions.
        var significantCells = tableCells
            .Where(c => c.Text.Trim().Length > 1 || !Regex.IsMatch(c.Text.Trim(), @"^[•\-\*\#]$"))
            .ToList();

        // If no significant cells remain, use all cells for clustering
        var cellsForClustering = significantCells.Count > 0 ? significantCells : tableCells;

        var allX = cellsForClustering.Select(c => c.X).OrderBy(x => x).ToList();
        var colTol = ComputeColumnTolerance(allX);
        var colCenters = ClusterValues(allX, colTol);

        foreach (var cell in tableCells)
        {
            cell.Column = GetNearestIndex(cell.X, colCenters);
            cell.Text = CorrectOcrPatterns(cell.Text);
        }

        foreach (var header in rowHeaderCells)
        {
            header.Column = -1;
            header.Text = CorrectOcrPatterns(header.Text);
        }

        var consolidatedTable = ConsolidateCellsByGrid(tableCells);
        var consolidatedRowHeaders = ConsolidateRowHeaders(rowHeaderCells);

        return consolidatedTable.Concat(consolidatedRowHeaders).ToList();
    }

    private static List<List<T>> GroupByProximity<T>(List<T> items, Func<T, double> selector, int tolerance)
    {
        var groups = new List<List<T>>();
        var orderedItems = items.OrderBy(selector).ToList();

        foreach (var item in orderedItems)
        {
            var itemValue = selector(item);
            var matchedGroup = default(List<T>);

            foreach (var group in groups)
            {
                var groupCenter = group.Average(selector);
                if (Math.Abs(itemValue - groupCenter) <= tolerance)
                {
                    matchedGroup = group;
                    break;
                }
            }

            if (matchedGroup == null)
            {
                groups.Add(new List<T> { item });
                continue;
            }

            matchedGroup.Add(item);
        }

        return groups;
    }

    private static int ComputeColumnTolerance(List<int> xPositions)
    {
        if (xPositions.Count < 2)
            return 10;

        var sorted = xPositions.OrderBy(x => x).ToList();
        var differences = new List<int>();

        for (var i = 1; i < sorted.Count; i++)
        {
            var diff = sorted[i] - sorted[i - 1];
            if (diff > 0)
                differences.Add(diff);
        }

        if (differences.Count == 0)
            return 10;

        var avgDiff = differences.OrderBy(x => x).ElementAt(differences.Count / 2);

        return Math.Max(5, (int)(avgDiff * 0.25));
    }

    private static List<int> ClusterValues(List<int> values, int tolerance)
    {
        if (values.Count == 0)
            return new List<int>();

        // Gap-based clustering: consecutive sorted positions stay in the same
        // cluster as long as the gap between them is within the tolerance. This
        // keeps a single left-aligned column together even when its left edge
        // drifts a few pixels across rows (different leading glyphs), instead of
        // splitting one visual column into several. A new cluster only starts
        // when a large horizontal gap between columns is encountered. The cluster
        // mean is returned so nearest-column mapping is centered on the column.
        var sorted = values.OrderBy(x => x).ToList();
        var clusters = new List<List<int>>();
        var current = new List<int> { sorted[0] };

        for (var i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] - sorted[i - 1] <= tolerance)
            {
                current.Add(sorted[i]);
                continue;
            }

            clusters.Add(current);
            current = new List<int> { sorted[i] };
        }

        clusters.Add(current);

        return clusters
            .Select(cluster => (int)Math.Round(cluster.Average()))
            .OrderBy(center => center)
            .ToList();
    }

    private static List<OcrTableCell> MergeAdjacentCellsHorizontally(List<OcrTableCell> cells, List<List<OcrTableCell>> rowGroups, int avgHeight)
    {
        var merged = new List<OcrTableCell>();

        foreach (var row in rowGroups)
        {
            var ordered = row.OrderBy(c => c.X).ToList();
            if (ordered.Count == 0)
                continue;

            var current = CloneCell(ordered[0]);

            for (var i = 1; i < ordered.Count; i++)
            {
                var next = ordered[i];
                var gap = next.X - (current.X + current.Width);
                var verticalDiff = Math.Abs(next.Y - current.Y);

                var shouldMerge = gap >= 0
                                  && gap <= Math.Max(18, avgHeight / 2)
                                  && verticalDiff <= Math.Max(8, avgHeight / 3)
                                  && ShouldTreatAsContinuation(current.Text, next.Text);

                if (shouldMerge)
                {
                    current.Text = string.IsNullOrWhiteSpace(current.Text)
                        ? next.Text
                        : $"{current.Text} {next.Text}";

                    var right = Math.Max(current.X + current.Width, next.X + next.Width);
                    var bottom = Math.Max(current.Y + current.Height, next.Y + next.Height);
                    current.Width = right - current.X;
                    current.Height = bottom - current.Y;
                    continue;
                }

                merged.Add(current);
                current = CloneCell(next);
            }

            merged.Add(current);
        }

        return merged;
    }

    private static bool ShouldTreatAsContinuation(string leftText, string rightText)
    {
        if (string.IsNullOrWhiteSpace(leftText) || string.IsNullOrWhiteSpace(rightText))
            return false;

        var left = leftText.Trim();
        var right = rightText.Trim();

        var leftNumeric = Regex.IsMatch(left, @"^[\d.,-]+$");
        var rightNumeric = Regex.IsMatch(right, @"^[\d.,-]+$");
        if (leftNumeric || rightNumeric)
            return false;

        var leftCompact = Regex.Replace(left, @"\s+", string.Empty);
        var rightCompact = Regex.Replace(right, @"\s+", string.Empty);

        // Never merge two complete coordinate-like tokens (e.g. R1C11 + R1C12)
        // because they are almost always different columns.
        var fullCoordPattern = @"^R[0-9OIl]{1,3}C[0-9OIl]{1,3}$";
        if (Regex.IsMatch(leftCompact, fullCoordPattern, RegexOptions.IgnoreCase)
            && Regex.IsMatch(rightCompact, fullCoordPattern, RegexOptions.IgnoreCase))
            return false;

        // Allow merges only for obvious split coordinate fragments from OCR, such as:
        // "RI" + "7C1", "R17C" + "1", "R" + "17C1".
        var leftIsPrefix = Regex.IsMatch(leftCompact, @"^R[0-9OIl]{0,3}C?$", RegexOptions.IgnoreCase);
        var rightLooksLikeSuffix = Regex.IsMatch(rightCompact, @"^[0-9OIl]{0,3}C[0-9OIl]{1,3}$", RegexOptions.IgnoreCase)
                                   || Regex.IsMatch(rightCompact, @"^[0-9OIl]{1,3}$", RegexOptions.IgnoreCase);
        var rightIsPrefix = Regex.IsMatch(rightCompact, @"^R[0-9OIl]{0,3}C?$", RegexOptions.IgnoreCase);
        var leftLooksLikeSuffix = Regex.IsMatch(leftCompact, @"^[0-9OIl]{0,3}C[0-9OIl]{1,3}$", RegexOptions.IgnoreCase)
                                  || Regex.IsMatch(leftCompact, @"^[0-9OIl]{1,3}$", RegexOptions.IgnoreCase);

        if ((leftIsPrefix && rightLooksLikeSuffix) || (rightIsPrefix && leftLooksLikeSuffix))
            return true;

        if (Regex.IsMatch(right, @"^(ID|[A-Za-z]\d?|\d?[A-Za-z])$", RegexOptions.IgnoreCase))
            return true;

        return false;
    }

    private static OcrTableCell CloneCell(OcrTableCell cell)
    {
        return new OcrTableCell
        {
            Text = cell.Text,
            X = cell.X,
            Y = cell.Y,
            Width = cell.Width,
            Height = cell.Height,
            Row = cell.Row,
            Column = cell.Column
        };
    }

    private static bool IsPotentialRowHeaderToken(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var token = text.Trim();
      
        if (token == "#")
            return true;

        return Regex.IsMatch(token, @"^\d{1,4}$");
    }

    private static List<OcrTableCell> ConsolidateRowHeaders(List<OcrTableCell> rowHeaderCells)
    {
        return rowHeaderCells
            .Where(c => c.Row >= 0)
            .GroupBy(c => c.Row)
            .Select(group =>
            {
                var ordered = group.OrderBy(c => c.X).ToList();
                var minX = ordered.Min(c => c.X);
                var minY = ordered.Min(c => c.Y);
                var maxRight = ordered.Max(c => c.X + c.Width);
                var maxBottom = ordered.Max(c => c.Y + c.Height);

                return new OcrTableCell
                {
                    Row = group.Key,
                    Column = -1,
                    Text = string.Join(" ", ordered.Select(c => c.Text?.Trim()).Where(t => !string.IsNullOrWhiteSpace(t))),
                    X = minX,
                    Y = minY,
                    Width = Math.Max(1, maxRight - minX),
                    Height = Math.Max(1, maxBottom - minY)
                };
            })
            .ToList();
    }

    private static List<OcrTableCell> ConsolidateCellsByGrid(List<OcrTableCell> cells)
    {
        return cells
            .Where(c => c.Row >= 0 && c.Column >= 0)
            .GroupBy(c => new { c.Row, c.Column })
            .Select(group =>
            {
                var ordered = group.OrderBy(c => c.X).ToList();
                var minX = ordered.Min(c => c.X);
                var minY = ordered.Min(c => c.Y);
                var maxRight = ordered.Max(c => c.X + c.Width);
                var maxBottom = ordered.Max(c => c.Y + c.Height);

                return new OcrTableCell
                {
                    Row = group.Key.Row,
                    Column = group.Key.Column,
                    Text = string.Join(" ", ordered.Select(c => c.Text?.Trim()).Where(t => !string.IsNullOrWhiteSpace(t))),
                    X = minX,
                    Y = minY,
                    Width = Math.Max(1, maxRight - minX),
                    Height = Math.Max(1, maxBottom - minY)
                };
            })
            .ToList();
    }

    private static List<OcrTableCell> ReindexColumnsDense(List<OcrTableCell> cells)
    {
        var colMap = cells
            .Select(c => c.Column)
            .Distinct()
            .OrderBy(c => c)
            .Select((original, index) => new { original, index })
            .ToDictionary(x => x.original, x => x.index);

        foreach (var cell in cells)
            cell.Column = colMap[cell.Column];

        return cells;
    }

    private static List<OcrTableCell> ReindexRowsDense(List<OcrTableCell> cells)
    {
        var rowMap = cells
            .Select(c => c.Row)
            .Distinct()
            .OrderBy(r => r)
            .Select((original, index) => new { original, index })
            .ToDictionary(x => x.original, x => x.index);

        foreach (var cell in cells)
            cell.Row = rowMap[cell.Row];

        return cells;
    }

    private static bool IsLikelyHeaderRow(List<OcrTableCell> rowCells)
    {
        if (rowCells == null || rowCells.Count == 0)
            return false;

        var tokens = rowCells
            .Select(c => (c.Text ?? string.Empty).Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        if (tokens.Count < 2)
            return false;

        var compactTokens = tokens
            .Select(t => Regex.Replace(t, @"\s+", string.Empty))
            .ToList();

        // Data rows from coordinate-like sheets (R0C0, R1C12, ...) must never be
        // promoted as headers even though they contain letters.
        var coordinateLikeCount = compactTokens.Count(t => Regex.IsMatch(t, @"^[Rr][0-9OIl]{1,3}[Cc(][0-9OIl]{1,3}$"));
        if (coordinateLikeCount >= Math.Max(2, (int)Math.Ceiling(compactTokens.Count * 0.5)))
            return false;

        var headerKeywords = new[]
        {
            "order", "id", "date", "customer", "product", "quantity", "unit", "units",
            "price", "total", "name", "description", "amount", "value", "revenue", "cost"
        };

        var keywordMatches = tokens.Count(text =>
        {
            var lower = text.ToLowerInvariant();
            return headerKeywords.Any(k => lower.Contains(k));
        });

        if (keywordMatches >= Math.Max(2, tokens.Count / 2))
            return true;

        var alphaLike = tokens.Count(t => t.Any(char.IsLetter) && !Regex.IsMatch(t, @"^-?\d+(?:[.,]\d+)?$"));
        var numericLike = tokens.Count(t => Regex.IsMatch(t, @"^-?\d+(?:[.,]\d+)?$"));

        return alphaLike >= Math.Max(2, (int)Math.Ceiling(tokens.Count * 0.6)) && numericLike == 0;
    }

    private static bool IsLikelyRxCyTable(List<OcrTableCell> cells)
    {
        // R×C table detection is disabled to prevent business tables from being
        // misclassified and filled with R#C# labels. If users need R×C support,
        // it should be opt-in rather than auto-detected.
        return false;
    }

    private static List<OcrTableCell> NormalizeAndFillRxCyTable(List<OcrTableCell> cells)
    {
        var byKey = cells.ToDictionary(c => (c.Row, c.Column), c => c);
        var minRow = cells.Min(c => c.Row);
        var maxRow = cells.Max(c => c.Row);
        var minCol = cells.Min(c => c.Column);
        var maxCol = cells.Max(c => c.Column);

        var normalized = new List<OcrTableCell>();

        for (var row = minRow; row <= maxRow; row++)
        for (var col = minCol; col <= maxCol; col++)
        {
            if (byKey.TryGetValue((row, col), out var existing))
            {
                existing.Text = $"R{row}C{col}";
                normalized.Add(existing);
                continue;
            }

            normalized.Add(new OcrTableCell
            {
                Row = row,
                Column = col,
                Text = $"R{row}C{col}",
                X = 0,
                Y = 0,
                Width = 1,
                Height = 1
            });
        }

        return normalized;
    }

    private static string CorrectOcrPatterns(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var corrected = text
            .Replace("(", "C");

        corrected = NormalizeLikelyIdToken(corrected);

        if (LooksLikeParameterIdentifier(corrected))
            corrected = NormalizeParameterIdentifier(corrected);

        corrected = corrected
            .Replace("SingIeturn", "Singleturn")
            .Replace("Identifiction", "Identification");

        corrected = NormalizeLikelyRxCyToken(corrected);

        return corrected;
    }

    private static string NormalizeLikelyIdToken(string text)
    {
        var token = text.Trim();
        if (token.Length != 2)
            return text;

        var second = char.ToUpperInvariant(token[1]);
        if (second != 'D')
            return text;

        var first = token[0];
        if (first == 'I' || first == 'i' || first == 'l' || first == 'L' || first == '1' || first == '|')
            return "ID";

        return text;
    }

    private static string NormalizeLikelyRxCyToken(string text)
    {
        var token = Regex.Replace(text.Trim(), @"\s+", string.Empty);
        if (!Regex.IsMatch(token, @"^[Rr][0-9OIl]{1,3}[Cc(][0-9OIl]{1,3}$"))
            return text;

        var normalized = new StringBuilder(token.Length);
        for (var i = 0; i < token.Length; i++)
        {
            var ch = token[i];

            if (i == 0)
            {
                normalized.Append('R');
                continue;
            }

            if (ch == 'c' || ch == '(')
            {
                normalized.Append('C');
                continue;
            }

            if (ch == 'O' || ch == 'o' || ch == 'Q' || ch == 'q')
            {
                normalized.Append('0');
                continue;
            }

            if (ch == 'I' || ch == 'i' || ch == 'l' || ch == '|')
            {
                normalized.Append('1');
                continue;
            }

            normalized.Append(ch);
        }

        return normalized.ToString();
    }

    private static bool LooksLikeParameterIdentifier(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var candidate = Regex.Replace(text.Trim(), @"\s+", string.Empty);
        return Regex.IsMatch(candidate, @"^[Pp][0-9OoQqIl|]+([\.,:;][0-9OoQqIl|]+){2,}$");
    }

    private static string NormalizeParameterIdentifier(string text)
    {
        var candidate = Regex.Replace(text.Trim(), @"\s+", string.Empty);

        candidate = Regex.Replace(candidate, "[,:;]", ".");
        candidate = Regex.Replace(candidate, "\\.{2,}", ".");

        if (candidate.Length == 0)
            return text;

        var normalized = new StringBuilder(candidate.Length);
        normalized.Append('P');

        for (var i = 1; i < candidate.Length; i++)
        {
            var ch = candidate[i];
            if (ch == 'O' || ch == 'o' || ch == 'Q' || ch == 'q')
            {
                normalized.Append('0');
                continue;
            }

            if (ch == 'I' || ch == 'i' || ch == 'l' || ch == '|')
            {
                normalized.Append('1');
                continue;
            }

            normalized.Append(ch);
        }

        return normalized.ToString();
    }

    private static int GetNearestIndex(int value, List<int> centers)
    {
        if (centers.Count == 0)
            return -1;

        return centers.IndexOf(centers.OrderBy(c => Math.Abs(c - value)).First());
    }
}
