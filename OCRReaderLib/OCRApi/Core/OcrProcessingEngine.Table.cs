using OCRReaderLib.OCRApi.DTOs;
using System.Text;
using System.Text.RegularExpressions;

namespace OCRReaderLib.OCRApi.Core;

public partial class OcrProcessingEngine
{
    private static OcrTableResult BuildTableResult(List<OcrTableCell> cells, float scaleFactor)
    {
        var result = new OcrTableResult();

        if (cells == null || cells.Count == 0)
            return result;

        // Prefer the dedicated parameter-export fallback first when a strong ID
        // pattern is present. This avoids pulling surrounding dialog/instruction
        // text into the table grid.
        if (TryBuildParameterExportTableFallback(cells, new OcrTableResult(), out var strictParameterFallback))
            return strictParameterFallback;

        var rowHeaderCandidates = ConsolidateRowHeaders(cells
            .Where(c => c.Row >= 0 && c.Column < 0 && !string.IsNullOrWhiteSpace(c.Text))
            .ToList());

        var valid = ConsolidateCellsByGrid(cells.Where(c => c.Column >= 0).ToList())
            .Where(c => !string.IsNullOrWhiteSpace(c.Text) && c.Row >= 0 && c.Column >= 0)
            .ToList();
      
        var hasSignificantData = valid.Any(c => c.Text.Trim().Length > 1 || System.Text.RegularExpressions.Regex.IsMatch(c.Text.Trim(), @"[A-Za-z0-9]{2,}"));
        if (hasSignificantData)
        {
            valid = valid
                .Where(c => c.Text.Trim().Length > 1 || !System.Text.RegularExpressions.Regex.IsMatch(c.Text.Trim(), @"^[a-z]$"))
                .ToList();
        }

        if (valid.Count == 0)
            return result;

        // R×C table normalization disabled - it was misclassifying business tables
        // if (IsLikelyRxCyTable(valid))
        //     valid = NormalizeAndFillRxCyTable(valid);

        valid = ReindexColumnsDense(valid);

        var orderedRows = valid.GroupBy(c => c.Row).OrderBy(g => g.Key).ToList();
        if (orderedRows.Count == 0)
            return result;

        var headerRowKey = orderedRows.First().Key;
        var headerColCount = orderedRows.First().Select(c => c.Column).Distinct().Count();
      
        var dataColCounts = orderedRows
            .Skip(1)
            .Select(g => g.Select(c => c.Column).Distinct().Count())
            .Where(count => count > 0)
            .ToList();

        var dominantDataColCount = dataColCounts.Count > 0
            ? dataColCounts
                .GroupBy(count => count)
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Key)
                .Select(g => g.Key)
                .First()
            : 0;

        var headerSpansData = dominantDataColCount == 0
            || headerColCount >= Math.Max(2, (int)Math.Ceiling(dominantDataColCount * 0.6));

        var isHeaderRow = headerSpansData && IsLikelyHeaderRow(orderedRows.First().ToList());

        if (isHeaderRow)
        {
            result.ColumnHeaders = orderedRows.First()
                .OrderBy(c => c.Column)
                .Select(CloneCell)
                .ToList();

            valid = valid.Where(c => c.Row != headerRowKey).ToList();
            rowHeaderCandidates = rowHeaderCandidates.Where(c => c.Row != headerRowKey).ToList();
        }

        var detectedHeaderCount = result.ColumnHeaders
            .Select(h => h.Column)
            .Distinct()
            .Count();

        var canTrustDetectedHeaders = result.ColumnHeaders.Count > 0
            && (dominantDataColCount == 0
                || detectedHeaderCount >= Math.Max(2, (int)Math.Ceiling(dominantDataColCount * 0.85)));

        if (canTrustDetectedHeaders)
        {
            var normalizedHeaders = result.ColumnHeaders
                .OrderBy(h => h.Column)
                .Select((h, index) => new OcrTableCell
                {
                    Text = h.Text,
                    Row = -1,
                    Column = index,
                    X = h.X,
                    Y = h.Y,
                    Width = h.Width,
                    Height = h.Height
                })
                .ToList();

            foreach (var cell in valid)
            {
                var nearest = normalizedHeaders
                    .OrderBy(h => Math.Abs(h.X - cell.X))
                    .First();
                cell.Column = nearest.Column;
            }

            valid = ConsolidateCellsByGrid(valid)
                .Where(c => c.Column >= 0 && c.Column < normalizedHeaders.Count)
                .ToList();

            result.ColumnHeaders = normalizedHeaders;
        }
        else
        {
            result.ColumnHeaders.Clear();
            var rowColCounts = valid
                .GroupBy(c => c.Row)
                .Select(g => g.Select(c => c.Column).Distinct().Count())
                .Where(count => count > 0)
                .ToList();

            var richDominantColCount = rowColCounts
                .Where(count => count >= 3)
                .GroupBy(count => count)
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Key)
                .Select(g => g.Key)
                .FirstOrDefault();

            var dominantColCount = richDominantColCount > 0
                ? richDominantColCount
                : rowColCounts
                    .GroupBy(count => count)
                    .OrderByDescending(g => g.Count())
                    .ThenByDescending(g => g.Key)
                    .Select(g => g.Key)
                    .FirstOrDefault();

            if (dominantColCount > 0)
            {
                // Do not hard-cut to dominant column count; OCR often misses a few
                // cells on the right side, which makes dominant count smaller than
                // the real table width and drops valid tail columns.
                var rowCount = valid.Select(c => c.Row).Distinct().Count();
                var minColumnSupport = Math.Max(2, (int)Math.Ceiling(rowCount * 0.2));

                var supportedColumns = valid
                    .GroupBy(c => c.Column)
                    .Where(g => g.Select(x => x.Row).Distinct().Count() >= minColumnSupport)
                    .Select(g => g.Key)
                    .ToHashSet();

                if (supportedColumns.Count > 0)
                    valid = valid.Where(c => supportedColumns.Contains(c.Column)).ToList();

                valid = ReindexColumnsDense(valid);
            }
        }

        NormalizeOrderDateCustomerColumns(valid, result.ColumnHeaders);

        var rowMap = valid
            .Select(c => c.Row)
            .Distinct()
            .OrderBy(r => r)
            .Select((original, index) => new { original, index })
            .ToDictionary(x => x.original, x => x.index);

        foreach (var cell in valid)
            cell.Row = rowMap[cell.Row];


        var mappedRowHeaders = rowHeaderCandidates
            .Where(c => rowMap.ContainsKey(c.Row))
            .GroupBy(c => rowMap[c.Row])
            .ToDictionary(
                g => g.Key,
                g => new OcrTableCell
                {
                    Row = g.Key,
                    Column = -1,
                    Text = string.Join(" ", g.OrderBy(x => x.X).Select(x => x.Text).Where(t => !string.IsNullOrWhiteSpace(t))),
                    X = g.Min(x => x.X),
                    Y = g.Min(x => x.Y),
                    Width = Math.Max(1, g.Max(x => x.X + x.Width) - g.Min(x => x.X)),
                    Height = Math.Max(1, g.Max(x => x.Y + x.Height) - g.Min(x => x.Y))
                });

        foreach (var rowKey in valid.Select(c => c.Row).Distinct().OrderBy(r => r))
            if (mappedRowHeaders.TryGetValue(rowKey, out var rowHeader))
                result.RowHeaders.Add(rowHeader);

        foreach (var rowGroup in valid.GroupBy(c => c.Row).OrderBy(g => g.Key))
        foreach (var cell in rowGroup.OrderBy(c => c.Column))
            result.Cells.Add(new OcrTableCell
            {
                Text = cell.Text,
                Row = cell.Row,
                Column = cell.Column,
                X = cell.X,
                Y = cell.Y,
                Width = cell.Width,
                Height = cell.Height
            });

        if (TryBuildParameterExportTableFallback(cells, result, out var fallbackResult))
            return fallbackResult;

        if (TryBuildPercentAxisTableFallback(cells, result, out var percentFallbackResult))
            return percentFallbackResult;

        return result;
    }

    private static void NormalizeOrderDateCustomerColumns(List<OcrTableCell> cells, List<OcrTableCell> headers)
    {
        if (cells == null || cells.Count == 0 || headers == null || headers.Count == 0)
            return;

        var orderColumn = FindColumnByHeader(headers, "order");
        var dateColumn = FindColumnByHeader(headers, "date");
        var customerColumn = FindColumnByHeader(headers, "customer");

        if (orderColumn < 0 || customerColumn < 0)
            return;

        if (dateColumn < 0)
        {
            // Infer a missing date column between detected order/customer columns,
            // but keep header text empty so DataTable uses generic fallback names.
            if (customerColumn <= orderColumn)
                return;

            dateColumn = customerColumn;

            foreach (var cell in cells.Where(c => c.Column >= dateColumn))
                cell.Column++;

            foreach (var header in headers.Where(h => h.Column >= dateColumn))
                header.Column++;

            var orderHeader = headers.FirstOrDefault(h => h.Column == orderColumn);
            var customerHeader = headers.FirstOrDefault(h => h.Column == dateColumn + 1);
            var inferredX = customerHeader != null
                ? customerHeader.X
                : (orderHeader != null ? orderHeader.X + orderHeader.Width + 20 : 0);

            headers.Add(new OcrTableCell
            {
                Row = -1,
                Column = dateColumn,
                Text = string.Empty,
                X = inferredX,
                Y = orderHeader?.Y ?? 0,
                Width = Math.Max(20, customerHeader?.Width ?? orderHeader?.Width ?? 40),
                Height = Math.Max(1, orderHeader?.Height ?? customerHeader?.Height ?? 1)
            });

            customerColumn++;
        }

        var datePattern = new Regex(@"\b20[0-9OIl]{2}[-/][0-9OIl]{2}[-/][0-9OIl]{2}\b", RegexOptions.IgnoreCase);

        var dateColumnX = cells.Where(c => c.Column == dateColumn).Select(c => (double)c.X).DefaultIfEmpty(double.NaN).Average();
        if (double.IsNaN(dateColumnX))
        {
            var dateHeader = headers.FirstOrDefault(h => h.Column == dateColumn);
            dateColumnX = dateHeader?.X ?? 0;
        }

        foreach (var rowGroup in cells.GroupBy(c => c.Row))
        {
            var rowCells = rowGroup.ToList();
            var orderCell = rowCells.FirstOrDefault(c => c.Column == orderColumn);
            var dateCell = rowCells.FirstOrDefault(c => c.Column == dateColumn);
            var customerCell = rowCells.FirstOrDefault(c => c.Column == customerColumn);

            if (orderCell == null)
                continue;

            var hasDate = dateCell != null && !string.IsNullOrWhiteSpace(dateCell.Text) && datePattern.IsMatch(dateCell.Text);
            if (hasDate)
                continue;

            string extractedDate = string.Empty;

            var orderText = orderCell.Text ?? string.Empty;
            var orderMatch = datePattern.Match(orderText);
            if (orderMatch.Success)
            {
                extractedDate = NormalizeDateToken(orderMatch.Value);
                orderCell.Text = Regex.Replace(orderText, Regex.Escape(orderMatch.Value), string.Empty).Trim();
            }
            else if (customerCell != null)
            {
                var customerText = customerCell.Text ?? string.Empty;
                var customerMatch = datePattern.Match(customerText);
                if (customerMatch.Success)
                {
                    extractedDate = NormalizeDateToken(customerMatch.Value);
                    customerCell.Text = Regex.Replace(customerText, Regex.Escape(customerMatch.Value), string.Empty).Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(extractedDate))
                continue;

            if (dateCell == null)
            {
                var seed = customerCell ?? orderCell;
                cells.Add(new OcrTableCell
                {
                    Row = rowGroup.Key,
                    Column = dateColumn,
                    Text = extractedDate,
                    X = (int)Math.Round(dateColumnX),
                    Y = seed.Y,
                    Width = Math.Max(1, seed.Width),
                    Height = Math.Max(1, seed.Height)
                });
            }
            else
            {
                dateCell.Text = extractedDate;
            }
        }
    }

    private static int FindColumnByHeader(List<OcrTableCell> headers, string keyword)
    {
        var match = headers
            .Where(h => h.Column >= 0 && !string.IsNullOrWhiteSpace(h.Text))
            .OrderBy(h => h.Column)
            .FirstOrDefault(h => h.Text.Contains(keyword, StringComparison.OrdinalIgnoreCase));

        return match?.Column ?? -1;
    }

    private static string NormalizeDateToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return token;

        var normalized = token.Trim().Replace('/', '-');
        normalized = normalized
            .Replace('O', '0')
            .Replace('o', '0')
            .Replace('Q', '0')
            .Replace('q', '0')
            .Replace('I', '1')
            .Replace('i', '1')
            .Replace('l', '1')
            .Replace('|', '1');

        return normalized;
    }

    private static bool TryBuildPercentAxisTableFallback(
        List<OcrTableCell> sourceCells,
        OcrTableResult currentResult,
        out OcrTableResult fallbackResult)
    {
        fallbackResult = new OcrTableResult();

        if (sourceCells == null || sourceCells.Count == 0)
            return false;

        if (!TryBuildInferredPercentAxis(sourceCells, out var inferredAxis) || inferredAxis.Count < 5)
            return false;

        var currentPercentCount = currentResult.Cells.Count(c => TryParsePercent(c.Text, out _));
        if (currentPercentCount >= inferredAxis.Count - 1)
            return false;

        for (var row = 0; row < inferredAxis.Count; row++)
            fallbackResult.Cells.Add(new OcrTableCell
            {
                Row = row,
                Column = 0,
                Text = inferredAxis[row],
                X = 0,
                Y = row,
                Width = 0,
                Height = 0
            });

        return fallbackResult.Cells.Count > currentResult.Cells.Count;
    }

    private static bool TryBuildParameterExportTableFallback(
        List<OcrTableCell> sourceCells,
        OcrTableResult currentResult,
        out OcrTableResult fallbackResult)
    {
        fallbackResult = new OcrTableResult();

        if (sourceCells == null || sourceCells.Count == 0)
            return false;

        var validCells = sourceCells
            .Where(c => !string.IsNullOrWhiteSpace(c.Text))
            .Select(CloneCell)
            .ToList();

        var idCells = validCells
            .Where(c => LooksLikeParameterId(c.Text))
            .OrderBy(c => c.Y)
            .ThenBy(c => c.X)
            .ToList();

        if (idCells.Count < 5)
            return false;

        var currentRowsWithThreeColumns = currentResult.Cells
            .GroupBy(c => c.Row)
            .Count(g => g.Count(c => !string.IsNullOrWhiteSpace(c.Text)) >= 3);

        if (currentRowsWithThreeColumns >= Math.Max(4, (int)(idCells.Count * 0.6)))
            return false;

        var rowTol = Math.Clamp((int)(idCells.Average(c => c.Height) * 0.9), 10, 28);
        TryDetectFallbackHeaderCells(validCells, idCells, rowTol, out var detectedHeaders);

        var columnCenters = BuildFallbackColumnCenters(validCells, idCells, detectedHeaders, rowTol);
        if (columnCenters.Count < 2)
            return false;

        var columnCount = Math.Max(columnCenters.Count, detectedHeaders.Count);

        for (var col = 0; col < columnCount; col++)
            fallbackResult.ColumnHeaders.Add(new OcrTableCell { Row = -1, Column = col, Text = string.Empty });

        foreach (var header in detectedHeaders)
        {
            var nearestColumn = GetNearestIndex(header.X, columnCenters);
            if (nearestColumn < 0 || nearestColumn >= columnCount)
                continue;

            var target = fallbackResult.ColumnHeaders[nearestColumn];
            var existingHeader = target.Text?.Trim();
            var incoming = header.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(incoming))
                continue;

            target.Text = string.IsNullOrWhiteSpace(existingHeader)
                ? incoming
                : existingHeader.Contains(incoming, StringComparison.OrdinalIgnoreCase)
                    ? existingHeader
                    : $"{existingHeader} {incoming}";

            // Preserve the detected header's spatial bounds so downstream logic can
            // tell the table region apart from surrounding text.
            if (target.Width <= 0 || target.Height <= 0)
            {
                target.X = header.X;
                target.Y = header.Y;
                target.Width = header.Width;
                target.Height = header.Height;
            }
            else
            {
                var right = Math.Max(target.X + target.Width, header.X + header.Width);
                var bottom = Math.Max(target.Y + target.Height, header.Y + header.Height);
                target.X = Math.Min(target.X, header.X);
                target.Y = Math.Min(target.Y, header.Y);
                target.Width = right - target.X;
                target.Height = bottom - target.Y;
            }
        }

        FillMissingHeadersFromNearestBand(validCells, idCells, rowTol, columnCenters, fallbackResult.ColumnHeaders);

        for (var rowIndex = 0; rowIndex < idCells.Count; rowIndex++)
        {
            var idCell = idCells[rowIndex];
            var idCenterY = idCell.Y + idCell.Height / 2.0;

            var sameRow = validCells
                .Where(c => Math.Abs((c.Y + c.Height / 2.0) - idCenterY) <= rowTol)
                .Where(c => !string.IsNullOrWhiteSpace(c.Text))
                .OrderBy(c => c.X)
                .ToList();

            if (sameRow.Count == 0)
                continue;

            var byColumn = new Dictionary<int, OcrTableCell>();

            foreach (var token in sameRow)
            {
                var columnIndex = GetNearestIndex(token.X, columnCenters);
                if (columnIndex < 0 || columnIndex >= columnCount)
                    continue;

                var normalizedText = CorrectOcrPatterns(token.Text?.Trim() ?? string.Empty);

                if (IsLikelyOcrZeroToken(normalizedText) && columnIndex >= 2)
                    normalizedText = "0";

                if (string.IsNullOrWhiteSpace(normalizedText) || !IsInformativeCellText(normalizedText))
                    continue;

                if (!byColumn.TryGetValue(columnIndex, out var existing))
                {
                    byColumn[columnIndex] = new OcrTableCell
                    {
                        Row = rowIndex,
                        Column = columnIndex,
                        Text = normalizedText,
                        X = token.X,
                        Y = token.Y,
                        Width = token.Width,
                        Height = token.Height
                    };
                    continue;
                }

                existing.Text = string.IsNullOrWhiteSpace(existing.Text)
                    ? normalizedText
                    : $"{existing.Text} {normalizedText}";

                var right = Math.Max(existing.X + existing.Width, token.X + token.Width);
                var bottom = Math.Max(existing.Y + existing.Height, token.Y + token.Height);
                existing.Width = right - existing.X;
                existing.Height = bottom - existing.Y;
            }

            foreach (var cell in byColumn.Values.OrderBy(c => c.Column))
                fallbackResult.Cells.Add(cell);
        }

        InferMissingHeadersFromColumnProfiles(fallbackResult);
        CompactFallbackColumns(fallbackResult);
        NormalizeParameterExportTableResult(fallbackResult);

        var fallbackNonEmpty = fallbackResult.Cells.Count(c => !string.IsNullOrWhiteSpace(c.Text));
        var currentNonEmpty = currentResult.Cells.Count(c => !string.IsNullOrWhiteSpace(c.Text));

        return fallbackNonEmpty > currentNonEmpty;
    }

    private static void NormalizeParameterExportTableResult(OcrTableResult result)
    {
        if (result.Cells.Count == 0)
            return;

        var columnKeys = result.Cells.Select(c => c.Column)
            .Concat(result.ColumnHeaders.Select(h => h.Column))
            .Distinct()
            .ToList();

        var columns = columnKeys
            .Select(col =>
            {
                var columnCells = result.Cells.Where(c => c.Column == col).ToList();
                var texts = columnCells
                    .Select(c => c.Text?.Trim())
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => t!)
                    .ToList();

                var header = result.ColumnHeaders.FirstOrDefault(h => h.Column == col);

                return new
                {
                    Column = col,
                    AvgX = columnCells.Count > 0 ? columnCells.Average(c => c.X) : header?.X ?? 0,
                    IdLike = texts.Count(LooksLikeParameterId),
                    ValueLike = texts.Count(LooksLikeValueCell),
                    NameLike = texts.Count(t => !LooksLikeParameterId(t) && t.Any(char.IsLetter) && !LooksLikeValueCell(t)),
                    Header = header
                };
            })
            .OrderBy(c => c.AvgX)
            .ToList();

        if (columns.Count < 3)
            return;

        var idColumn = columns
            .OrderByDescending(c => c.IdLike)
            .ThenBy(c => c.AvgX)
            .First();

        if (idColumn.IdLike < 2)
            return;

        var nameColumn = columns
            .Where(c => c.Column != idColumn.Column && c.AvgX > idColumn.AvgX)
            .OrderByDescending(c => c.NameLike)
            .ThenBy(c => c.AvgX)
            .FirstOrDefault();

        if (nameColumn == null)
            return;

        static bool HasValueHeader(string? text) =>
            !string.IsNullOrWhiteSpace(text) &&
            (text!.Contains("value", StringComparison.OrdinalIgnoreCase)
             || text.Contains("import", StringComparison.OrdinalIgnoreCase)
             || text.Contains("default", StringComparison.OrdinalIgnoreCase)
             || text.Contains("current", StringComparison.OrdinalIgnoreCase));

        // Value columns sit to the right of the name column, or are identified by a
        // detected value-style header even when OCR missed their (faint) cell values.
        var valueColumns = columns
            .Where(c => c.Column != idColumn.Column && c.Column != nameColumn.Column)
            .Where(c => c.AvgX > nameColumn.AvgX || HasValueHeader(c.Header?.Text))
            .OrderBy(c => c.AvgX)
            .ToList();

        if (valueColumns.Count == 0)
            return;

        var orderedColumns = new[] { idColumn, nameColumn }
            .Concat(valueColumns)
            .ToList();

        var selectedMap = orderedColumns
            .Select((profile, index) => new { profile.Column, index })
            .ToDictionary(x => x.Column, x => x.index);

        var normalizedCells = result.Cells
            .Where(c => selectedMap.ContainsKey(c.Column))
            .Select(c => new OcrTableCell
            {
                Row = c.Row,
                Column = selectedMap[c.Column],
                Text = c.Text,
                X = c.X,
                Y = c.Y,
                Width = c.Width,
                Height = c.Height
            })
            .GroupBy(c => new { c.Row, c.Column })
            .Select(group => new OcrTableCell
            {
                Row = group.Key.Row,
                Column = group.Key.Column,
                Text = string.Join(" ", group.OrderBy(c => c.X).Select(c => c.Text).Where(t => !string.IsNullOrWhiteSpace(t))),
                X = group.Min(c => c.X),
                Y = group.Min(c => c.Y),
                Width = Math.Max(1, group.Max(c => c.X + c.Width) - group.Min(c => c.X)),
                Height = Math.Max(1, group.Max(c => c.Y + c.Height) - group.Min(c => c.Y))
            })
            .ToList();

        // Mirror a detected value across the sibling value columns of the same row
        // (e.g. OCR read the Imported "0"/"False" but missed the identical Default one).
        // Rows where no value was detected at all are left empty rather than fabricated.
        var valueColumnIndexes = Enumerable.Range(2, valueColumns.Count).ToList();
        var rows = normalizedCells.Select(c => c.Row).Distinct().OrderBy(r => r).ToList();

        foreach (var row in rows)
        {
            var rowValueCells = valueColumnIndexes
                .ToDictionary(ci => ci, ci => normalizedCells.FirstOrDefault(c => c.Row == row && c.Column == ci));

            var knownValue = rowValueCells.Values
                .Select(c => c?.Text?.Trim())
                .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t) && LooksLikeValueCell(t!));

            if (string.IsNullOrWhiteSpace(knownValue))
                continue;

            foreach (var ci in valueColumnIndexes)
            {
                var existing = rowValueCells[ci];
                if (existing != null && !string.IsNullOrWhiteSpace(existing.Text))
                    continue;

                normalizedCells.Add(new OcrTableCell
                {
                    Row = row,
                    Column = ci,
                    Text = knownValue!,
                    X = 0,
                    Y = 0,
                    Width = 0,
                    Height = 0
                });
            }
        }

        result.Cells = normalizedCells
            .GroupBy(c => new { c.Row, c.Column })
            .Select(group => group.OrderByDescending(c => c.Width * c.Height).ThenBy(c => c.X).First())
            .OrderBy(c => c.Row)
            .ThenBy(c => c.Column)
            .ToList();

        // Rebuild headers from the OCR-detected header text (no hardcoded names),
        // rejecting any text that is actually a data value that slipped into the header.
        var dataValues = new HashSet<string>(
            result.Cells
                .Select(c => c.Text?.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t!),
            StringComparer.OrdinalIgnoreCase);

        result.ColumnHeaders = orderedColumns
            .Select((profile, index) =>
            {
                var header = profile.Header;
                var text = header?.Text?.Trim() ?? string.Empty;

                var isDataLike = LooksLikeParameterId(text)
                                 || LooksLikeValueCell(text)
                                 || dataValues.Contains(text);

                if (isDataLike)
                    text = string.Empty;

                if (index == 0 && string.IsNullOrWhiteSpace(text) && profile.IdLike >= 2)
                    text = "ID";

                return new OcrTableCell
                {
                    Row = -1,
                    Column = index,
                    Text = text,
                    X = header?.X ?? 0,
                    Y = header?.Y ?? 0,
                    Width = header?.Width ?? 0,
                    Height = header?.Height ?? 0
                };
            })
            .ToList();
    }

    private static void CompactFallbackColumns(OcrTableResult result)
    {
        var rowCount = result.Cells.Select(c => c.Row).Distinct().Count();
        if (rowCount == 0 || result.ColumnHeaders.Count == 0)
            return;

        var columnStats = result.ColumnHeaders
            .Select(header =>
            {
                var nonEmpty = result.Cells.Count(c => c.Column == header.Column && !string.IsNullOrWhiteSpace(c.Text));
                var informative = result.Cells.Count(c => c.Column == header.Column && IsInformativeCellText(c.Text));
                var hasDetectedHeader = !string.IsNullOrWhiteSpace(header.Text);
                return new { header.Column, NonEmpty = nonEmpty, Informative = informative, HasDetectedHeader = hasDetectedHeader };
            })
            .OrderBy(x => x.Column)
            .ToList();

        const int minUsefulNonEmpty = 2;

        var strongColumns = columnStats
            .Where(stat => stat.HasDetectedHeader || stat.Informative >= minUsefulNonEmpty)
            .Select(stat => stat.Column)
            .ToList();

        var lastStrongColumn = strongColumns.Count > 0 ? strongColumns.Max() : -1;

        var keepColumns = columnStats
            .Where(stat => lastStrongColumn < 0 || stat.Column <= lastStrongColumn)
            .Where(stat => stat.HasDetectedHeader || stat.Informative >= minUsefulNonEmpty)
            .Select(stat => stat.Column)
            .Distinct()
            .OrderBy(col => col)
            .ToList();

        if (keepColumns.Count == 0)
            return;

        var colMap = keepColumns
            .Select((col, index) => new { col, index })
            .ToDictionary(x => x.col, x => x.index);

        result.ColumnHeaders = result.ColumnHeaders
            .Where(h => colMap.ContainsKey(h.Column))
            .OrderBy(h => h.Column)
            .Select(h => new OcrTableCell
            {
                Row = -1,
                Column = colMap[h.Column],
                Text = h.Text,
                X = h.X,
                Y = h.Y,
                Width = h.Width,
                Height = h.Height
            })
            .ToList();

        result.Cells = result.Cells
            .Where(c => colMap.ContainsKey(c.Column))
            .Select(c => new OcrTableCell
            {
                Row = c.Row,
                Column = colMap[c.Column],
                Text = c.Text,
                X = c.X,
                Y = c.Y,
                Width = c.Width,
                Height = c.Height
            })
            .ToList();

        if (result.ColumnHeaders.All(h => string.IsNullOrWhiteSpace(h.Text)))
            result.ColumnHeaders.Clear();
    }

    private static bool TryDetectFallbackHeaderCells(
        List<OcrTableCell> validCells,
        List<OcrTableCell> idCells,
        int rowTol,
        out List<OcrTableCell> headerCells)
    {
        headerCells = new List<OcrTableCell>();

        if (validCells.Count == 0 || idCells.Count == 0)
            return false;

        var firstIdCenterY = idCells.Min(c => c.Y + c.Height / 2.0);
        var headerBand = validCells
            .Where(c => c.Y + c.Height / 2.0 < firstIdCenterY)
            .Where(c => c.Y + c.Height / 2.0 >= firstIdCenterY - rowTol * 8)
            .ToList();

        if (headerBand.Count == 0)
            return false;

        var groupedRows = GroupByProximity(headerBand, c => c.Y + c.Height / 2.0, rowTol)
            .OrderByDescending(row => row.Average(c => c.Y + c.Height / 2.0))
            .ToList();

        for (var i = 0; i < groupedRows.Count; i++)
        {
            var mergedRow = groupedRows[i].ToList();

            if (i + 1 < groupedRows.Count)
            {
                var currentY = groupedRows[i].Average(c => c.Y + c.Height / 2.0);
                var nextY = groupedRows[i + 1].Average(c => c.Y + c.Height / 2.0);

                if (Math.Abs(currentY - nextY) <= rowTol)
                    mergedRow.AddRange(groupedRows[i + 1]);
            }

            var candidate = mergedRow
                .Where(c => !string.IsNullOrWhiteSpace(c.Text))
                .Select(c => new OcrTableCell
                {
                    Row = c.Row,
                    Column = c.Column,
                    Text = CorrectOcrPatterns(c.Text),
                    X = c.X,
                    Y = c.Y,
                    Width = c.Width,
                    Height = c.Height
                })
                .Where(c => IsLikelyTableHeaderToken(c.Text))
                .OrderBy(c => c.X)
                .ToList();

            candidate = MergeHeaderCells(candidate);

            if (candidate.Count < 2 || candidate.Count > 8)
                continue;

            headerCells = candidate
                .Select((cell, index) => new OcrTableCell
                {
                    Row = -1,
                    Column = index,
                    Text = cell.Text,
                    X = cell.X,
                    Y = cell.Y,
                    Width = cell.Width,
                    Height = cell.Height
                })
                .ToList();

            return true;
        }

        return false;
    }

    private static List<OcrTableCell> MergeHeaderCells(List<OcrTableCell> cells)
    {
        if (cells.Count <= 1)
            return cells;

        var merged = new List<OcrTableCell>();
        var current = CloneCell(cells[0]);

        for (var i = 1; i < cells.Count; i++)
        {
            var next = cells[i];
            var gap = next.X - (current.X + current.Width);
            var similarY = Math.Abs(next.Y - current.Y) <= Math.Max(6, current.Height / 2);

            if (similarY && gap >= 0 && gap <= Math.Max(20, current.Height))
            {
                current.Text = $"{current.Text} {next.Text}".Trim();
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
        return merged;
    }

    private static bool IsLikelyTableHeaderToken(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var token = text.Trim();
        if (token.Length > 25)
            return false;

        if (LooksLikeParameterId(token) || LooksLikeValueCell(token))
            return false;

        return token.Any(char.IsLetter);
    }

    private static bool LooksLikeParameterId(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return Regex.IsMatch(text.Trim(), @"^[Pp][0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$");
    }

    private static List<int> BuildFallbackColumnCenters(
        List<OcrTableCell> validCells,
        List<OcrTableCell> idCells,
        List<OcrTableCell> detectedHeaders,
        int rowTol)
    {
        var centers = new List<int>();

        if (detectedHeaders.Count > 0)
            centers.AddRange(detectedHeaders.OrderBy(h => h.X).Select(h => h.X));

        var seedRows = idCells.Take(8).ToList();
        foreach (var idCell in seedRows)
        {
            var rowCenterY = idCell.Y + idCell.Height / 2.0;
            var rowCells = validCells
                .Where(c => Math.Abs((c.Y + c.Height / 2.0) - rowCenterY) <= rowTol)
                .Where(c => !string.IsNullOrWhiteSpace(c.Text))
                .OrderBy(c => c.X)
                .ToList();

            foreach (var cell in rowCells)
                centers.Add(cell.X);

            var normalizedId = NormalizeParameterIdentifier(idCell.Text ?? string.Empty);
            var idToken = rowCells.FirstOrDefault(c => NormalizeParameterIdentifier(c.Text ?? string.Empty) == normalizedId);
            if (idToken != null)
                centers.Add(idToken.X);

            var firstAfterId = rowCells
                .Where(c => c.X > idCell.X + idCell.Width)
                .FirstOrDefault();

            if (firstAfterId != null)
                centers.Add(firstAfterId.X);
        }

        if (centers.Count == 0)
            return new List<int>();

        var tolerance = ComputeColumnTolerance(centers.OrderBy(x => x).ToList());
        var clustered = ClusterValues(centers, Math.Clamp(tolerance, 6, 14));

        return clustered.OrderBy(x => x).ToList();
    }

    private static void FillMissingHeadersFromNearestBand(
        List<OcrTableCell> validCells,
        List<OcrTableCell> idCells,
        int rowTol,
        List<int> columnCenters,
        List<OcrTableCell> headers)
    {
        if (validCells.Count == 0 || idCells.Count == 0 || headers.Count == 0 || columnCenters.Count == 0)
            return;

        var firstIdCenterY = idCells.Min(c => c.Y + c.Height / 2.0);
        var nearestHeaderBand = validCells
            .Where(c => c.Y + c.Height / 2.0 < firstIdCenterY)
            .Where(c => c.Y + c.Height / 2.0 >= firstIdCenterY - rowTol * 3)
            .Where(c => !string.IsNullOrWhiteSpace(c.Text))
            .ToList();

        if (nearestHeaderBand.Count == 0)
            return;

        foreach (var header in headers.Where(h => string.IsNullOrWhiteSpace(h.Text)))
        {
            if (header.Column < 0 || header.Column >= columnCenters.Count)
                continue;

            var centerX = columnCenters[header.Column];
            var candidate = nearestHeaderBand
                .Where(c => IsLikelyTableHeaderToken(c.Text))
                .OrderBy(c => Math.Abs(c.X - centerX))
                .ThenByDescending(c => c.Y)
                .FirstOrDefault();

            if (candidate == null)
                continue;

            if (Math.Abs(candidate.X - centerX) > Math.Max(80, candidate.Width * 3))
                continue;

            header.Text = CorrectOcrPatterns(candidate.Text);

            // Preserve the source coordinates so the header band is part of the
            // table's spatial extent (prevents header labels leaking into the
            // surrounding-text extraction).
            header.X = candidate.X;
            header.Y = candidate.Y;
            header.Width = candidate.Width;
            header.Height = candidate.Height;
        }
    }

    private static bool IsLikelyOcrZeroToken(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var token = text.Trim();
        return token.Length == 1 && (token == "O" || token == "o" || token == "Q" || token == "q");
    }

    private static void InferMissingHeadersFromColumnProfiles(OcrTableResult result)
    {
        if (result.ColumnHeaders.Count == 0 || result.Cells.Count == 0)
            return;

        var blankHeaderProfiles = result.ColumnHeaders
            .Where(h => string.IsNullOrWhiteSpace(h.Text))
            .Select(h =>
            {
                var columnCells = result.Cells
                    .Where(c => c.Column == h.Column && !string.IsNullOrWhiteSpace(c.Text))
                    .ToList();

                var idLikeCount = columnCells.Count(c => LooksLikeParameterId(c.Text));
                var ratio = columnCells.Count == 0 ? 0.0 : (double)idLikeCount / columnCells.Count;

                return new
                {
                    Header = h,
                    CellCount = columnCells.Count,
                    IdLikeCount = idLikeCount,
                    IdRatio = ratio
                };
            })
            .ToList();

        if (blankHeaderProfiles.Count == 0)
            return;

        var bestIdColumn = blankHeaderProfiles
            .OrderByDescending(p => p.IdLikeCount)
            .ThenByDescending(p => p.IdRatio)
            .First();

        if (bestIdColumn.IdLikeCount >= 2 && bestIdColumn.IdRatio >= 0.4)
            bestIdColumn.Header.Text = "ID";
    }

    private static bool IsInformativeCellText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var candidate = text.Trim();
        return candidate.Any(char.IsLetterOrDigit);
    }

    private static bool LooksLikeValueCell(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var candidate = text.Trim();

        if (Regex.IsMatch(candidate, @"^(true|false)$", RegexOptions.IgnoreCase))
            return true;

        return Regex.IsMatch(candidate, @"^-?\d+(?:[.,]\d+)?$");
    }

    private static OcrTableData ConvertTableResultToDto(OcrTableResult tableResult)
    {
        var dto = new OcrTableData();

        if (tableResult?.Cells != null)
            dto.Cells = tableResult.Cells.Select(c => new OcrTableCell
            {
                Text = c.Text,
                Row = c.Row,
                Column = c.Column,
                X = c.X,
                Y = c.Y,
                Width = c.Width,
                Height = c.Height
            }).ToList();

        if (tableResult?.ColumnHeaders != null)
            dto.ColumnHeaders = tableResult.ColumnHeaders.Select(c => new OcrTableCell
            {
                Text = c.Text,
                Row = c.Row,
                Column = c.Column,
                X = c.X,
                Y = c.Y,
                Width = c.Width,
                Height = c.Height
            }).ToList();

        if (tableResult?.RowHeaders != null)
            dto.RowHeaders = tableResult.RowHeaders.Select(c => new OcrTableCell
            {
                Text = c.Text,
                Row = c.Row,
                Column = c.Column,
                X = c.X,
                Y = c.Y,
                Width = c.Width,
                Height = c.Height
            }).ToList();

        dto.RowCount = tableResult?.Cells.GroupBy(c => c.Row).Count() ?? 0;
        dto.ColumnCount = tableResult?.Cells.GroupBy(c => c.Column).Count() ?? 0;

        return dto;
    }
}

internal class OcrTableResult
{
    public List<OcrTableCell> Cells { get; set; } = new();
    public List<OcrTableCell> ColumnHeaders { get; set; } = new();
    public List<OcrTableCell> RowHeaders { get; set; } = new();

    public override string ToString()
    {
        if (Cells.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        var cellsByRow = Cells.GroupBy(c => c.Row).OrderBy(g => g.Key).ToList();
        var maxCol = Cells.Max(c => c.Column);
        var hasRowHeaders = RowHeaders.Count > 0;

        if (ColumnHeaders.Count > 0)
        {
            if (hasRowHeaders)
                sb.Append("#\t");

            for (var i = 0; i <= maxCol; i++)
            {
                var header = ColumnHeaders.FirstOrDefault(c => c.Column == i);
                if (i > 0)
                    sb.Append("\t");
                sb.Append(header?.Text ?? string.Empty);
            }

            sb.AppendLine();
            sb.AppendLine(new string('─', 80));
        }

        foreach (var rowGroup in cellsByRow)
        {
            var row = rowGroup.ToList();

            if (hasRowHeaders)
            {
                var rowHeader = RowHeaders.FirstOrDefault(h => h.Row == rowGroup.Key)?.Text;
                sb.Append(string.IsNullOrWhiteSpace(rowHeader) ? (rowGroup.Key + 1).ToString() : rowHeader);
                sb.Append("\t");
            }

            for (var col = 0; col <= maxCol; col++)
            {
                var cell = row.FirstOrDefault(c => c.Column == col);
                if (col > 0)
                    sb.Append("\t");
                sb.Append(cell?.Text ?? string.Empty);
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }
}
