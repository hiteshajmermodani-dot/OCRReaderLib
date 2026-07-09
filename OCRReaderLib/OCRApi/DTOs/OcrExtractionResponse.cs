using System.Data;

namespace OCRReaderLib.OCRApi.DTOs
{
    /// <summary>
    /// Response object for OCR extraction results
    /// </summary>
    public class OcrExtractionResponse
    {
        /// <summary>
        /// Whether extraction was successful
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Detected extraction mode
        /// </summary>
        public ImageExtractionMode DetectedMode { get; set; }

        /// <summary>
        /// Extracted content as string
        /// </summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// Extracted table data (when in Table mode)
        /// </summary>
        public OcrTableData TableData { get; set; } = new OcrTableData();

        /// <summary>
        /// Error message if extraction failed
        /// </summary>
        public string ErrorMessage { get; set; } = string.Empty;

        /// <summary>
        /// Processing time in milliseconds
        /// </summary>
        public long ProcessingTimeMs { get; set; }

        /// <summary>
        /// Number of text elements detected
        /// </summary>
        public int ElementCount { get; set; }
    }

    /// <summary>
    /// Table extraction data
    /// </summary>
    public class OcrTableData
    {
        /// <summary>
        /// Table cells organized with row/column information
        /// </summary>
        public List<OcrTableCell> Cells { get; set; } = new List<OcrTableCell>();

        /// <summary>
        /// Column headers
        /// </summary>
        public List<OcrTableCell> ColumnHeaders { get; set; } = new List<OcrTableCell>();

        /// <summary>
        /// Row headers
        /// </summary>
        public List<OcrTableCell> RowHeaders { get; set; } = new List<OcrTableCell>();

        /// <summary>
        /// Number of rows detected
        /// </summary>
        public int RowCount { get; set; }

        /// <summary>
        /// Number of columns detected
        /// </summary>
        public int ColumnCount { get; set; }

        /// <summary>
        /// Converts OCR table cells to a DataTable.
        /// </summary>
        public DataTable ToDataTable()
        {
            var table = new DataTable();
            var cells = Cells
                .Where(c => c.Row >= 0 && c.Column >= 0)
                .ToList();

            if (cells.Count == 0)
                return table;

            var maxRow = cells.Max(c => c.Row);

            var headerByColumn = ColumnHeaders
                .Where(h => h.Column >= 0)
                .GroupBy(h => h.Column)
                .ToDictionary(g => g.Key, g => g.First().Text?.Trim() ?? string.Empty);

            var allColumns = cells.Select(c => c.Column)
                .Concat(headerByColumn.Keys)
                .Distinct()
                .OrderBy(c => c)
                .ToList();

            var stats = allColumns
                .Select(col =>
                {
                    var columnCells = cells.Where(c => c.Column == col).ToList();
                    return new ColumnProjection
                    {
                        SourceColumn = col,
                        Header = headerByColumn.TryGetValue(col, out var header) ? header : string.Empty,
                        NonEmptyCount = columnCells.Count(c => !string.IsNullOrWhiteSpace(c.Text)),
                        AvgX = columnCells.Count > 0 ? columnCells.Average(c => c.X) : col * 100
                    };
                })
                .ToList();

            foreach (var headerOnly in stats.Where(s => s.NonEmptyCount == 0 && !string.IsNullOrWhiteSpace(s.Header)).ToList())
            {
                var target = stats
                    .Where(s => s.NonEmptyCount > 0 && string.IsNullOrWhiteSpace(s.Header) && Math.Abs(s.SourceColumn - headerOnly.SourceColumn) <= 1)
                    .OrderBy(s => Math.Abs(s.SourceColumn - headerOnly.SourceColumn))
                    .ThenByDescending(s => s.NonEmptyCount)
                    .FirstOrDefault();

                if (target == null)
                    continue;

                target.Header = headerOnly.Header;
                headerOnly.Remove = true;
            }

            var selected = stats
                .Where(s => !s.Remove)
                .Where(s => s.NonEmptyCount > 0 || !string.IsNullOrWhiteSpace(s.Header))
                .OrderBy(s => s.SourceColumn)
                .ToList();

            if (selected.Count == 0)
                return table;

            var headerCandidates = ColumnHeaders
                .Where(h => h.Column >= 0 && !string.IsNullOrWhiteSpace(h.Text))
                .Select(h => new { Header = h.Text.Trim(), X = (double)h.X })
                .GroupBy(h => h.Header, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(x => x.X).First())
                .OrderBy(h => h.X)
                .ToList();

            var orderedTargets = selected
                .OrderBy(s => s.AvgX)
                .ToList();

            var avgSpacing = orderedTargets.Count > 1
                ? orderedTargets.Zip(orderedTargets.Skip(1), (left, right) => Math.Abs(right.AvgX - left.AvgX)).Average()
                : 120.0;

            var maxAssignDistance = Math.Max(80.0, avgSpacing * 1.35);
            var unassignedTargets = orderedTargets.ToList();

            foreach (var header in headerCandidates)
            {
                if (unassignedTargets.Count == 0)
                    break;

                var target = unassignedTargets
                    .OrderBy(s => Math.Abs(s.AvgX - header.X))
                    .ThenByDescending(s => s.NonEmptyCount)
                    .First();

                if (Math.Abs(target.AvgX - header.X) > maxAssignDistance)
                    continue;

                if (string.IsNullOrWhiteSpace(target.Header))
                    target.Header = header.Header;

                unassignedTargets.Remove(target);
            }

            var assignedHeaders = selected
                .Where(s => !string.IsNullOrWhiteSpace(s.Header))
                .Select(s => s.Header.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var header in headerCandidates.Where(h => !assignedHeaders.Contains(h.Header)))
            {
                var target = selected
                    .Where(s => string.IsNullOrWhiteSpace(s.Header))
                    .OrderBy(s => Math.Abs(s.AvgX - header.X))
                    .FirstOrDefault();

                if (target == null)
                    continue;

                if (Math.Abs(target.AvgX - header.X) <= maxAssignDistance * 2.0)
                    target.Header = header.Header;
            }

            NormalizeProjectedHeaders(selected);
            selected = selected.Where(s => !s.Remove).OrderBy(s => s.SourceColumn).ToList();

            var promoteFirstRowAsHeaders = false;

            var columnMap = selected
                .Select((s, index) => new { s.SourceColumn, index })
                .ToDictionary(x => x.SourceColumn, x => x.index);

            var rowHeaderByRow = RowHeaders
                .Where(h => h.Row >= 0 && !string.IsNullOrWhiteSpace(h.Text))
                .GroupBy(h => h.Row)
                .ToDictionary(g => g.Key, g => string.Join(" ", g.Select(x => x.Text?.Trim()).Where(t => !string.IsNullOrWhiteSpace(t))));

            var includeRowHeaders = rowHeaderByRow.Count > 0;
            var rowHeaderOffset = includeRowHeaders ? 1 : 0;

            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (includeRowHeaders)
            {
                table.Columns.Add("#", typeof(string));
                usedNames.Add("#");
            }

            for (var i = 0; i < selected.Count; i++)
            {
                var baseName = !string.IsNullOrWhiteSpace(selected[i].Header)
                    ? selected[i].Header
                    : $"Column {i + 1}";

                var columnName = baseName;
                var suffix = 2;
                while (!usedNames.Add(columnName))
                {
                    columnName = $"{baseName} ({suffix})";
                    suffix++;
                }

                table.Columns.Add(columnName, typeof(string));
            }

            var firstDataRow = promoteFirstRowAsHeaders ? 1 : 0;

            for (var row = firstDataRow; row <= maxRow; row++)
            {
                var dataRow = table.NewRow();

                if (includeRowHeaders && rowHeaderByRow.TryGetValue(row, out var rowHeaderText))
                    dataRow[0] = rowHeaderText;

                foreach (var cell in cells.Where(c => c.Row == row))
                {
                    if (!columnMap.TryGetValue(cell.Column, out var mappedColumn))
                        continue;

                    dataRow[mappedColumn + rowHeaderOffset] = cell.Text ?? string.Empty;
                }

                table.Rows.Add(dataRow);
            }

            return table;
        }

        private static void NormalizeProjectedHeaders(List<ColumnProjection> selected)
        {
            if (selected == null || selected.Count == 0)
                return;

            foreach (var projection in selected)
            {
                projection.Header = projection.Header?.Trim() ?? string.Empty;
            }

            // Generic de-duplication only: if two adjacent projected columns have the
            // exact same non-empty header and one is effectively empty, drop the sparse one.
            var byColumn = selected.OrderBy(s => s.SourceColumn).ToList();
            for (var i = 1; i < byColumn.Count; i++)
            {
                var left = byColumn[i - 1];
                var right = byColumn[i];

                if (string.IsNullOrWhiteSpace(left.Header) || string.IsNullOrWhiteSpace(right.Header))
                    continue;

                if (!string.Equals(left.Header, right.Header, StringComparison.OrdinalIgnoreCase))
                    continue;

                var removeRight = right.NonEmptyCount <= left.NonEmptyCount;
                if (removeRight)
                    right.Remove = true;
                else
                    left.Remove = true;
            }
        }

        private static bool IsHeaderLikeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var token = text.Trim();
            var hasLetter = token.Any(char.IsLetter);
            var isPureNumber = System.Text.RegularExpressions.Regex.IsMatch(token, @"^-?\d+(?:[.,]\d+)?$");
            return hasLetter && !isPureNumber;
        }

        private static bool IsDataLikeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var token = text.Trim();
            if (System.Text.RegularExpressions.Regex.IsMatch(token, @"^-?\d+(?:[.,]\d+)?$"))
                return true;
           
            return token.Equals("true", StringComparison.OrdinalIgnoreCase)
                   || token.Equals("false", StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLikeParameterIdToken(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return System.Text.RegularExpressions.Regex.IsMatch(text.Trim(), @"^[Pp][0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$");
        }

        private sealed class ColumnProjection
        {
            public int SourceColumn { get; set; }
            public string Header { get; set; } = string.Empty;
            public int NonEmptyCount { get; set; }
            public double AvgX { get; set; }
            public bool Remove { get; set; }
        }

        /// <summary>
        /// Converts OCR table cells to a DataView.
        /// </summary>
        public DataView ToDataView()
        {
            return ToDataTable().DefaultView;
        }
    }

    /// <summary>
    /// Individual cell in OCR table
    /// </summary>
    public class OcrTableCell
    {
        /// <summary>
        /// Cell text content
        /// </summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>
        /// Row index (0-based)
        /// </summary>
        public int Row { get; set; }

        /// <summary>
        /// Column index (0-based)
        /// </summary>
        public int Column { get; set; }

        /// <summary>
        /// X coordinate in image
        /// </summary>
        public int X { get; set; }

        /// <summary>
        /// Y coordinate in image
        /// </summary>
        public int Y { get; set; }

        /// <summary>
        /// Cell width in pixels
        /// </summary>
        public int Width { get; set; }

        /// <summary>
        /// Cell height in pixels
        /// </summary>
        public int Height { get; set; }
    }
}
