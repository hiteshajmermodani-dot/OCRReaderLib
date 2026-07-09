namespace OCRReaderLib.OCRApi.DTOs
{
    /// <summary>
    /// Request object for OCR text extraction
    /// </summary>
    public class OcrExtractionRequest
    {
        /// <summary>
        /// Full path to the image file to process
        /// </summary>
        public string ImagePath { get; set; } = string.Empty;

        /// <summary>
        /// Extraction mode: Auto, PlainText, or Table
        /// </summary>
        public ImageExtractionMode ExtractionMode { get; set; } = ImageExtractionMode.Auto;
    }

    /// <summary>
    /// Image extraction modes
    /// </summary>
    public enum ImageExtractionMode
    {
        /// <summary>
        /// Automatically detect whether content is plain text or tabular
        /// </summary>
        Auto = 0,

        /// <summary>
        /// Extract content as structured table with rows and columns
        /// </summary>
        Table = 1,

        /// <summary>
        /// Extract content as plain continuous text
        /// </summary>
        PlainText = 2
    }
}
