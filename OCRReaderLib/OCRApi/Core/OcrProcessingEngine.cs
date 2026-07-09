using OCRReaderLib.OCRApi.DTOs;
using System.Diagnostics;
using System.Drawing;
using Windows.Media.Ocr;

namespace OCRReaderLib.OCRApi.Core;

/// <summary>
/// Core OCR processing engine with image preprocessing and text detection
/// </summary>
public partial class OcrProcessingEngine
{
    private const int RowNumMaxXBase = 50;
    private const int OcrMaxDimension = 4800;
    private const float MinUpscaleFactor = 1.0f;
    private const float MaxUpscaleFactor = 4.0f;
    private const float SparseTextMaxUpscaleFactor = 6.0f;

    /// <summary>
    /// Process image and extract text asynchronously
    /// </summary>
    public async Task<OcrExtractionResponse> ProcessImageAsync(OcrExtractionRequest request)
    {
        var stopwatch = Stopwatch.StartNew();
        var response = new OcrExtractionResponse();

        try
        {
            if (string.IsNullOrWhiteSpace(request?.ImagePath))
            {
                response.Success = false;
                response.ErrorMessage = "Image path cannot be null or empty.";
                return response;
            }

            if (!File.Exists(request.ImagePath))
            {
                response.Success = false;
                response.ErrorMessage = $"Image file not found: {request.ImagePath}";
                return response;
            }

            // Run OCR engine
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine == null)
            {
                response.Success = false;
                response.ErrorMessage = "OCR engine not available on this system.";
                return response;
            }

            // Load image and run primary OCR pass
            using var original = new Bitmap(request.ImagePath);
            var scaleFactor = ComputeSafeUpscaleFactor(original.Width, original.Height);

            using var processed = PreprocessBitmap(original, scaleFactor);
            var softwareBitmap = await ConvertToSoftwareBitmapAsync(processed);
            var ocrResult = await engine.RecognizeAsync(softwareBitmap);
            var rawCells = ParseOcrResultToCells(ocrResult, processed.Width, processed.Height);

            // Retry sparse/faint OCR output with stronger preprocessing.
            if (ShouldRetrySparseRecognition(rawCells, ocrResult))
            {
                var bestOcrResult = ocrResult;
                var bestCells = rawCells;
                var bestScale = scaleFactor;

                var fallbackScale = ComputeSafeUpscaleFactor(original.Width, original.Height, SparseTextMaxUpscaleFactor);
                var attemptScale = Math.Max(scaleFactor, fallbackScale);

                using (var fallbackProcessed = PreprocessBitmapForSparseText(original, attemptScale))
                {
                    var fallbackBitmap = await ConvertToSoftwareBitmapAsync(fallbackProcessed);
                    var fallbackOcrResult = await engine.RecognizeAsync(fallbackBitmap);
                    var fallbackCells = ParseOcrResultToCells(fallbackOcrResult, fallbackProcessed.Width, fallbackProcessed.Height);

                    if (IsBetterRecognition(fallbackCells, fallbackOcrResult, bestCells, bestOcrResult))
                    {
                        bestOcrResult = fallbackOcrResult;
                        bestCells = fallbackCells;
                        bestScale = attemptScale;
                    }
                }

                using (var binaryProcessed = PreprocessBitmapForSparseTextBinary(original, attemptScale))
                {
                    var binaryBitmap = await ConvertToSoftwareBitmapAsync(binaryProcessed);
                    var binaryOcrResult = await engine.RecognizeAsync(binaryBitmap);
                    var binaryCells = ParseOcrResultToCells(binaryOcrResult, binaryProcessed.Width, binaryProcessed.Height);

                    if (IsBetterRecognition(binaryCells, binaryOcrResult, bestCells, bestOcrResult))
                    {
                        bestOcrResult = binaryOcrResult;
                        bestCells = binaryCells;
                        bestScale = attemptScale;
                    }
                }

                ocrResult = bestOcrResult;
                rawCells = bestCells;
                scaleFactor = bestScale;
            }

            response.ElementCount = rawCells.Count;

            // Determine extraction mode
            var resolvedMode = request.ExtractionMode == ImageExtractionMode.Auto
                ? DetectContentType(rawCells)
                : request.ExtractionMode;

            response.DetectedMode = resolvedMode;

            // Extract content based on mode
            if (resolvedMode == ImageExtractionMode.PlainText)
            {
                response.Content = BuildPlainText(ocrResult, rawCells);
            }
            else
            {
                var tableResult = BuildTableResult(rawCells, scaleFactor);
                response.TableData = ConvertTableResultToDto(tableResult);

                var surroundingText = ExtractSurroundingTextOutsideTable(ocrResult, tableResult);
                response.Content = string.IsNullOrWhiteSpace(surroundingText)
                    ? string.Empty
                    : surroundingText;
            }

            response.Success = true;
            stopwatch.Stop();
            response.ProcessingTimeMs = stopwatch.ElapsedMilliseconds;

            return response;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            response.Success = false;
            response.ErrorMessage = ex.Message;
            response.ProcessingTimeMs = stopwatch.ElapsedMilliseconds;
            return response;
        }
    }
}
