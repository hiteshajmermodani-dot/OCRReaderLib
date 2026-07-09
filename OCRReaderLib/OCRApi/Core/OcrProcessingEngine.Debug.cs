using OCRReaderLib.OCRApi.DTOs;
using System.Drawing;
using System.Text;

namespace OCRReaderLib.OCRApi.Core;

public partial class OcrProcessingEngine
{
    public static async Task<string> DebugExtractAsync(string imagePath)
    {
        var cells = await ExtractCellTextAsync(imagePath);

        var orderedCells = cells
            .OrderBy(x => x.Y)
            .ThenBy(x => x.X)
            .ToList();

        var builder = new StringBuilder();

        builder.AppendLine("=== RAW OCR CELLS (scaled coords) ===");

        var textWidth = Math.Max(
            "Text".Length,
            orderedCells.Count == 0
                ? 0
                : orderedCells.Max(c => c.Text?.Length ?? 0));

        var xWidth = Math.Max(
            "X".Length,
            orderedCells.Count == 0
                ? 0
                : orderedCells.Max(c => c.X.ToString().Length));

        var yWidth = Math.Max(
            "Y".Length,
            orderedCells.Count == 0
                ? 0
                : orderedCells.Max(c => c.Y.ToString().Length));

        var wWidth = Math.Max(
            "W".Length,
            orderedCells.Count == 0
                ? 0
                : orderedCells.Max(c => c.Width.ToString().Length));

        var hWidth = Math.Max(
            "H".Length,
            orderedCells.Count == 0
                ? 0
                : orderedCells.Max(c => c.Height.ToString().Length));

        textWidth += 2;
        xWidth += 2;
        yWidth += 2;
        wWidth += 2;
        hWidth += 2;

        var header =
            "Text".PadRight(textWidth) +
            "X".PadLeft(xWidth) +
            "Y".PadLeft(yWidth) +
            "W".PadLeft(wWidth) +
            "H".PadLeft(hWidth);

        builder.AppendLine(header);
        builder.AppendLine(new string('-', header.Length));

        foreach (var cell in orderedCells)
            builder.AppendLine(
                (cell.Text ?? string.Empty).PadRight(textWidth) +
                cell.X.ToString().PadLeft(xWidth) +
                cell.Y.ToString().PadLeft(yWidth) +
                cell.Width.ToString().PadLeft(wWidth) +
                cell.Height.ToString().PadLeft(hWidth));

        return builder.ToString();
    }

    private static async Task<List<OcrTableCell>> ExtractCellTextAsync(string imagePath)
    {
        var cells = new List<OcrTableCell>();

        try
        {
            using var original = new Bitmap(imagePath);
            var scaleFactor = ComputeSafeUpscaleFactor(original.Width, original.Height);

            using var processed = PreprocessBitmap(original, scaleFactor);
            var softwareBitmap = await ConvertToSoftwareBitmapAsync(processed);

            var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine == null)
                return cells;

            var ocrResult = await engine.RecognizeAsync(softwareBitmap);
            cells = ParseOcrResultToCells(ocrResult, processed.Width, processed.Height);
        }
        catch
        {
            // Return empty list on error
        }

        return cells;
    }
}
