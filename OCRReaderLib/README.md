# OCRReaderLib

Core OCR extraction library for the OCRReader solution (.NET 8).

## What it does
- Extracts plain text from images.
- Extracts table data (rows, columns, headers, row headers).
- Supports automatic mode detection between plain text and table output.

## Main components
- `OCRApi/Core/OcrProcessingEngine.*`
  - OCR parsing and normalization
  - Table detection/building
  - Plain-text extraction
- `OCRApi/DTOs/OcrExtractionResponse.cs`
  - Table/response DTOs
  - Converts OCR table cells to `DataTable` / `DataView`

## Output behavior
- Column headers come from OCR-detected headers when available.
- If headers are not detected, generic fallback names are used (`Column 1`, `Column 2`, ...).
- Row headers are included in table output when detected.

## Notes
- No hardcoded business-specific column headers should be used.
- Keep extraction logic generic and image-driven.

## Code snippet
```csharp
using OCRReaderLib.OCRApi;
using OCRReaderLib.OCRApi.DTOs;

var service = new OcrService();
var imagePath = @"C:\images\sample.png";

// Extract as table
OcrExtractionResponse tableResult = await service.ExtractAsync(imagePath, ImageExtractionMode.Table);
if (tableResult.Success)
{
    var tableView = tableResult.TableData.ToDataView(); // bind to DataGrid
    var extractedText = tableResult.Content;
}

// Extract as plain text
OcrExtractionResponse textResult = await service.ExtractAsync(imagePath, ImageExtractionMode.PlainText);
if (textResult.Success)
{
    Console.WriteLine(textResult.Content);
}
```
