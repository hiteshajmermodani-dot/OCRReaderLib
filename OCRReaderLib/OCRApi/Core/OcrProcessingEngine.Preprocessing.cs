using OCRReaderLib.OCRApi.DTOs;
using System.Drawing;
using Windows.Media.Ocr;

namespace OCRReaderLib.OCRApi.Core;

public partial class OcrProcessingEngine
{
    private static float ComputeSafeUpscaleFactor(int width, int height, float maxUpscaleFactor = MaxUpscaleFactor)
    {
        if (width <= 0 || height <= 0)
            return 1.0f;

        var longestSide = Math.Max(width, height);
        var adaptiveLimit = longestSide switch
        {
            >= 1600 => 1.0f,
            >= 1200 => 1.25f,
            >= 900 => 1.5f,
            >= 700 => 2.0f,
            >= 500 => 2.5f,
            _ => Math.Min(maxUpscaleFactor, 3.0f)
        };

        var maxByWidth = (float)OcrMaxDimension / width;
        var maxByHeight = (float)OcrMaxDimension / height;

        var boundedMaxUpscale = Math.Max(
            MinUpscaleFactor,
            Math.Min(maxUpscaleFactor, adaptiveLimit));

        var factor = Math.Min(boundedMaxUpscale, Math.Min(maxByWidth, maxByHeight));
        return Math.Max(MinUpscaleFactor, factor);
    }

    private static Bitmap PreprocessBitmap(Bitmap source, float scale)
    {
        var newWidth = (int)(source.Width * scale);
        var newHeight = (int)(source.Height * scale);

        if (newWidth > OcrMaxDimension || newHeight > OcrMaxDimension)
        {
            var safeScale = ComputeSafeUpscaleFactor(source.Width, source.Height);
            newWidth = (int)(source.Width * safeScale);
            newHeight = (int)(source.Height * safeScale);
        }

        var upscaled = new Bitmap(newWidth, newHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        upscaled.SetResolution(source.HorizontalResolution, source.VerticalResolution);

        using (var graphics = Graphics.FromImage(upscaled))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            graphics.DrawImage(source, 0, 0, newWidth, newHeight);
        }

        var enhanced = ApplySimpleContrast(upscaled);
        upscaled.Dispose();

        return enhanced;
    }

    private static Bitmap ApplySimpleContrast(Bitmap source)
    {
        var result = new Bitmap(source.Width, source.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        using (var graphics = Graphics.FromImage(result))
        {
            var colorMatrix = new System.Drawing.Imaging.ColorMatrix(new[]
            {
                new[] { 1.15f, 0f, 0f, 0f, 0f },
                new[] { 0f, 1.15f, 0f, 0f, 0f },
                new[] { 0f, 0f, 1.15f, 0f, 0f },
                new float[] { 0f, 0f, 0f, 1f, 0f },
                new[] { 0f, 0f, 0f, 0f, 1f }
            });

            var imageAttributes = new System.Drawing.Imaging.ImageAttributes();
            imageAttributes.SetColorMatrix(colorMatrix, System.Drawing.Imaging.ColorMatrixFlag.Default,
                System.Drawing.Imaging.ColorAdjustType.Bitmap);

            graphics.DrawImage(
                source,
                new Rectangle(0, 0, source.Width, source.Height),
                0, 0,
                source.Width, source.Height,
                GraphicsUnit.Pixel,
                imageAttributes);
        }

        return result;
    }

    internal static Bitmap PreprocessBitmapForSparseText(Bitmap source, float scale)
    {
        var newWidth = (int)(source.Width * scale);
        var newHeight = (int)(source.Height * scale);

        if (newWidth > OcrMaxDimension || newHeight > OcrMaxDimension)
        {
            var safeScale = ComputeSafeUpscaleFactor(source.Width, source.Height, SparseTextMaxUpscaleFactor);
            newWidth = (int)(source.Width * safeScale);
            newHeight = (int)(source.Height * safeScale);
        }

        var upscaled = new Bitmap(newWidth, newHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        upscaled.SetResolution(source.HorizontalResolution, source.VerticalResolution);

        using (var graphics = Graphics.FromImage(upscaled))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            graphics.DrawImage(source, 0, 0, newWidth, newHeight);
        }

        var enhanced = ApplyStrongGrayContrast(upscaled);
        upscaled.Dispose();

        return enhanced;
    }

    private static Bitmap ApplyStrongGrayContrast(Bitmap source)
    {
        var result = new Bitmap(source.Width, source.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        using (var graphics = Graphics.FromImage(result))
        {
            const float contrast = 1.45f;
            var offset = (1f - contrast) / 2f + 0.08f;

            var colorMatrix = new System.Drawing.Imaging.ColorMatrix(new[]
            {
                new[] { 0.299f * contrast, 0.299f * contrast, 0.299f * contrast, 0f, 0f },
                new[] { 0.587f * contrast, 0.587f * contrast, 0.587f * contrast, 0f, 0f },
                new[] { 0.114f * contrast, 0.114f * contrast, 0.114f * contrast, 0f, 0f },
                new float[] { 0f, 0f, 0f, 1f, 0f },
                new[] { offset, offset, offset, 0f, 1f }
            });

            var imageAttributes = new System.Drawing.Imaging.ImageAttributes();
            imageAttributes.SetColorMatrix(colorMatrix, System.Drawing.Imaging.ColorMatrixFlag.Default,
                System.Drawing.Imaging.ColorAdjustType.Bitmap);

            graphics.DrawImage(
                source,
                new Rectangle(0, 0, source.Width, source.Height),
                0, 0,
                source.Width, source.Height,
                GraphicsUnit.Pixel,
                imageAttributes);
        }

        return result;
    }

    internal static Bitmap PreprocessBitmapForSparseTextBinary(Bitmap source, float scale)
    {
        var newWidth = (int)(source.Width * scale);
        var newHeight = (int)(source.Height * scale);

        if (newWidth > OcrMaxDimension || newHeight > OcrMaxDimension)
        {
            var safeScale = ComputeSafeUpscaleFactor(source.Width, source.Height, SparseTextMaxUpscaleFactor);
            newWidth = (int)(source.Width * safeScale);
            newHeight = (int)(source.Height * safeScale);
        }

        var upscaled = new Bitmap(newWidth, newHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        upscaled.SetResolution(source.HorizontalResolution, source.VerticalResolution);

        using (var graphics = Graphics.FromImage(upscaled))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            graphics.DrawImage(source, 0, 0, newWidth, newHeight);
        }

        var enhanced = ApplyBinaryThreshold(upscaled, 190);
        upscaled.Dispose();

        return enhanced;
    }

    internal static Bitmap ApplyBinaryThreshold(Bitmap source, byte threshold)
    {
        var result = new Bitmap(source.Width, source.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++)
        {
            var color = source.GetPixel(x, y);
            var luminance = (byte)(0.299 * color.R + 0.587 * color.G + 0.114 * color.B);
            var binary = luminance < threshold ? (byte)0 : (byte)255;
            result.SetPixel(x, y, Color.FromArgb(color.A, binary, binary, binary));
        }

        return result;
    }

    private static bool ShouldRetrySparseRecognition(List<OcrTableCell> cells, OcrResult ocrResult)
    {
        var validCellCount = cells?.Count(c => !string.IsNullOrWhiteSpace(c.Text)) ?? 0;
        var wordCount = CountRecognizedWords(ocrResult);
        var lineCount = ocrResult?.Lines?.Count ?? 0;
        var charCount = ocrResult?.Lines?.Sum(line => line.Words?.Sum(word => word.Text?.Length ?? 0) ?? 0) ?? 0;

        var idLikeCount = cells?.Count(c => LooksLikeParameterId(c.Text)) ?? 0;
        var valueLikeCount = cells?.Count(c => LooksLikeValueCell(c.Text)) ?? 0;
        var parameterTableValueSparse = idLikeCount >= 8 && valueLikeCount <= Math.Max(4, idLikeCount / 2);

        return validCellCount <= 4
               || wordCount <= 12
               || (lineCount <= 4 && charCount < 120)
               || parameterTableValueSparse;
    }

    private static bool IsBetterRecognition(
        List<OcrTableCell> candidateCells,
        OcrResult candidateResult,
        List<OcrTableCell> baselineCells,
        OcrResult baselineResult)
    {
        var candidateScore = ComputeRecognitionScore(candidateCells, candidateResult);
        var baselineScore = ComputeRecognitionScore(baselineCells, baselineResult);

        return candidateScore > baselineScore;
    }

    private static int ComputeRecognitionScore(List<OcrTableCell> cells, OcrResult ocrResult)
    {
        var validCellCount = cells?.Count(c => !string.IsNullOrWhiteSpace(c.Text)) ?? 0;
        var wordCount = CountRecognizedWords(ocrResult);
        var charCount = ocrResult?.Lines?.Sum(line => line.Words?.Sum(word => word.Text?.Length ?? 0) ?? 0) ?? 0;

        return validCellCount * 6 + wordCount * 3 + charCount;
    }

    private static int CountRecognizedWords(OcrResult ocrResult)
    {
        return ocrResult?.Lines?.Sum(line => line.Words?.Count ?? 0) ?? 0;
    }

    private static async Task<Windows.Graphics.Imaging.SoftwareBitmap> ConvertToSoftwareBitmapAsync(Bitmap bitmap)
    {
        var tempPath = Path.GetTempFileName();
        bitmap.Save(tempPath);

        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(tempPath);
        var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);

        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        return await decoder.GetSoftwareBitmapAsync();
    }
}
