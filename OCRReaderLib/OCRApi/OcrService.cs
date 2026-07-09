using OCRReaderLib.OCRApi.Core;
using OCRReaderLib.OCRApi.DTOs;

namespace OCRReaderLib.OCRApi;

/// <summary>
/// High-level OCR service API for application-wide use
/// </summary>
public class OcrService : IDisposable
{
    private readonly OcrProcessingEngine _engine;
    private bool _disposed = false;

    /// <summary>
    /// Initializes a new instance of the OcrService class
    /// </summary>
    public OcrService()
    {
        _engine = new OcrProcessingEngine();
    }

    /// <summary>
    /// Extract text from image file asynchronously
    /// </summary>
    /// <param name="imagePath">Full path to image file</param>
    /// <param name="mode">Extraction mode (Auto, PlainText, or Table)</param>
    /// <returns>Extraction response with results or error details</returns>
    public async Task<OcrExtractionResponse> ExtractAsync(
        string imagePath,
        ImageExtractionMode mode = ImageExtractionMode.Auto)
    {
        ThrowIfDisposed();

        var request = new OcrExtractionRequest
        {
            ImagePath = imagePath,
            ExtractionMode = mode
        };

        return await _engine.ProcessImageAsync(request);
    }

    /// <summary>
    /// Extract text from image file with custom request asynchronously
    /// </summary>
    /// <param name="request">Detailed extraction request</param>
    /// <returns>Extraction response with results or error details</returns>
    public async Task<OcrExtractionResponse> ExtractAsync(OcrExtractionRequest request)
    {
        ThrowIfDisposed();

        if (request == null)
            throw new ArgumentNullException(nameof(request));

        return await _engine.ProcessImageAsync(request);
    }

    /// <summary>
    /// Get plain text from image
    /// </summary>
    public async Task<string?> ExtractPlainTextAsync(string imagePath)
    {
        var response = await ExtractAsync(imagePath, ImageExtractionMode.PlainText);
        return response.Success ? response.Content : null;
    }

    /// <summary>
    /// Get table data from image
    /// </summary>
    public async Task<OcrTableData?> ExtractTableAsync(string imagePath)
    {
        var response = await ExtractAsync(imagePath, ImageExtractionMode.Table);
        return response.Success ? response.TableData : null;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(OcrService));
    }
}