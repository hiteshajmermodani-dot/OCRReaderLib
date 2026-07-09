using Microsoft.Win32;
using OCRReader.Infrastructure.Commands;
using OCRReader.Infrastructure.MVVM;
using OCRReaderLib.OCRApi;
using OCRReaderLib.OCRApi.DTOs;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace OCRReader.ViewModels;

/// <summary>
///     ViewModel for OCR Reader application - handles data binding and commands
/// </summary>
public class OcrViewModel : ViewModelBase
{
    private readonly OcrService _ocrService;
    private string _extractedText;
    private bool _isProcessing;
    private BitmapImage? _selectedImage;
    private string? _selectedImagePath;
    private string _statusMessage;
    private bool _isPlainTextMode;
    private bool _isTableMode;
    private bool _isAutoMode;
    private string? _cachedImagePath;
    private bool _isExtracting;
    private DataView? _tablePreview;

    public OcrViewModel()
    {
        _ocrService = new OcrService();
        _extractedText = string.Empty;
        _statusMessage = "Ready to load image...";
        _isPlainTextMode = true;
        _isTableMode = false;
        _isAutoMode = false;
        _cachedImagePath = null;

        ImportImageCommand = new RelayCommand(ImportImage, CanImportImage);
        ClearCommand = new RelayCommand(Clear, CanClear);
        CopyTextCommand = new RelayCommand(CopyToClipboard, CanCopyText);
    }

    #region Properties

    /// <summary>
    ///     Extracted text from the loaded image
    /// </summary>
    public string ExtractedText
    {
        get => _extractedText;
        set
        {
            if (!SetProperty(ref _extractedText, value))
                return;

            OnPropertyChanged(nameof(ShowPlainTextResult));
            OnPropertyChanged(nameof(ShowPlainTextOnly));
            OnPropertyChanged(nameof(ShowTextAboveTable));

            // Trigger command re-query when extracted text changes
            // This ensures the Copy button is enabled/disabled based on text availability
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    ///     Selected image for display
    /// </summary>
    public BitmapImage? SelectedImage
    {
        get => _selectedImage;
        set => SetProperty(ref _selectedImage, value);
    }

    public DataView? TablePreview
    {
        get => _tablePreview;
        set
        {
            if (!SetProperty(ref _tablePreview, value))
                return;

            OnPropertyChanged(nameof(HasTableData));
            OnPropertyChanged(nameof(ShowPlainTextResult));
            OnPropertyChanged(nameof(ShowPlainTextOnly));
            OnPropertyChanged(nameof(ShowTextAboveTable));
        }
    }

    public bool HasTableData => TablePreview != null && TablePreview.Count > 0;

    public bool ShowPlainTextResult => !string.IsNullOrWhiteSpace(ExtractedText);

    public bool ShowPlainTextOnly => ShowPlainTextResult && !HasTableData;

    public bool ShowTextAboveTable => ShowPlainTextResult && HasTableData;

    /// <summary>
    ///     Path to the selected image file
    /// </summary>
    public string? SelectedImagePath
    {
        get => _selectedImagePath;
        set => SetProperty(ref _selectedImagePath, value);
    }

    /// <summary>
    ///     Indicates if OCR processing is in progress
    /// </summary>
    public bool IsProcessing
    {
        get => _isProcessing;
        set
        {
            if (SetProperty(ref _isProcessing, value))
                // Trigger command re-query when processing state changes
                // This ensures buttons are updated when processing completes
                CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    ///     Status message for user feedback
    /// </summary>
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>
    ///     Indicates if Plain Text extraction mode is selected
    /// </summary>
    public bool IsPlainTextMode
    {
        get => _isPlainTextMode;
        set
        {
            if (!SetProperty(ref _isPlainTextMode, value))
                return;

            if (!value)
                return;

            SetExtractionModeFlags(ImageExtractionMode.PlainText);

            if (!string.IsNullOrEmpty(_cachedImagePath) && !_isExtracting)
                TriggerExtractionAsync();
        }
    }

    /// <summary>
    ///     Indicates if Table extraction mode is selected
    /// </summary>
    public bool IsTableMode
    {
        get => _isTableMode;
        set
        {
            if (!SetProperty(ref _isTableMode, value))
                return;

            if (!value)
                return;

            SetExtractionModeFlags(ImageExtractionMode.Table);

            if (!string.IsNullOrEmpty(_cachedImagePath) && !_isExtracting)
                TriggerExtractionAsync();
        }
    }

    /// <summary>
    ///     Indicates if Auto extraction mode is selected
    /// </summary>
    public bool IsAutoMode
    {
        get => _isAutoMode;
        set
        {
            if (!SetProperty(ref _isAutoMode, value))
                return;

            if (!value)
                return;

            SetExtractionModeFlags(ImageExtractionMode.Auto);

            if (!string.IsNullOrEmpty(_cachedImagePath) && !_isExtracting)
                TriggerExtractionAsync();
        }
    }

    #endregion

    #region Commands

    public ICommand ImportImageCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand CopyTextCommand { get; }

    #endregion

    #region Command Methods

    /// <summary>
    ///     Imports an image and performs OCR
    /// </summary>
    private async void ImportImage()
    {
        var dialog = new OpenFileDialog
        {
            Filter =
                "Image files (*.jpg, *.jpeg, *.png, *.bmp, *.gif, *.tiff)|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tiff|All files (*.*)|*.*",
            Title = "Select an image to extract text from"
        };

        if (dialog.ShowDialog() == true)
            try
            {
                IsProcessing = true;
                StatusMessage = "Loading image...";

                // Store the image path for re-extraction on mode changes
                _cachedImagePath = dialog.FileName;
                SelectedImagePath = dialog.FileName;

                // Load image preview
                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.UriSource = new Uri(dialog.FileName);
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.EndInit();
                bitmapImage.Freeze();

                SelectedImage = bitmapImage;

                StatusMessage = "Performing OCR...";

                // Perform extraction with the current extraction mode
                await PerformExtractionAsync();
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
                ExtractedText = $"Error processing image: {ex.Message}\n\nDetails: {ex.InnerException?.Message}";
            }
            finally
            {
                IsProcessing = false;
            }
    }

    /// <summary>
    ///     Helper method to trigger extraction with guard to prevent race conditions
    /// </summary>
    private void TriggerExtractionAsync()
    {
        if (_isExtracting)
            return; // Prevent multiple simultaneous extractions

        _ = PerformExtractionAsync();
    }

    private void SetExtractionModeFlags(ImageExtractionMode selectedMode)
    {
        var shouldBePlain = selectedMode == ImageExtractionMode.PlainText;
        var shouldBeTable = selectedMode == ImageExtractionMode.Table;
        var shouldBeAuto = selectedMode == ImageExtractionMode.Auto;

        if (_isPlainTextMode != shouldBePlain)
            SetProperty(ref _isPlainTextMode, shouldBePlain, nameof(IsPlainTextMode));

        if (_isTableMode != shouldBeTable)
            SetProperty(ref _isTableMode, shouldBeTable, nameof(IsTableMode));

        if (_isAutoMode != shouldBeAuto)
            SetProperty(ref _isAutoMode, shouldBeAuto, nameof(IsAutoMode));
    }

    /// <summary>
    ///     Performs OCR extraction using the cached image path and current extraction mode
    /// </summary>
    private async Task PerformExtractionAsync()
    {
        if (string.IsNullOrEmpty(_cachedImagePath))
        {
            StatusMessage = "No image loaded. Please import an image first.";
            return;
        }

        try
        {
            _isExtracting = true;
            IsProcessing = true;
            StatusMessage = "Performing OCR...";

            var mode = IsAutoMode
                ? ImageExtractionMode.Auto
                : IsTableMode
                    ? ImageExtractionMode.Table
                    : ImageExtractionMode.PlainText;

            Debug.WriteLine($"[ViewModel] Extraction mode: {mode}");

            var response = await _ocrService.ExtractAsync(_cachedImagePath, mode);

            if (response.Success)
            {
                ExtractedText = response.Content;
                TablePreview = response.TableData?.ToDataView();
                StatusMessage =
                    $"OCR completed successfully! ({response.ElementCount} elements, {response.ProcessingTimeMs}ms)";
            }
            else
            {
                TablePreview = null;
                ExtractedText = $"Error during extraction: {response.ErrorMessage}";
                StatusMessage = $"Error: {response.ErrorMessage}";
            }
        }
        catch (Exception ex)
        {
            TablePreview = null;
            StatusMessage = $"Error: {ex.Message}";
            ExtractedText = $"Error processing image: {ex.Message}\n\nDetails: {ex.InnerException?.Message}";
        }
        finally
        {
            _isExtracting = false;
            IsProcessing = false;
        }
    }

    /// <summary>
    ///  Clears all data and resets the UI
    /// </summary>
    private void Clear()
    {
        ExtractedText = string.Empty;
        TablePreview = null;
        SelectedImage = null;
        SelectedImagePath = null;
        _cachedImagePath = null; // Clear the cached image path
        StatusMessage = "Ready to load image...";
    }

    /// <summary>
    ///     Copies extracted text or table dataset to clipboard
    /// </summary>
    private void CopyToClipboard()
    {
        try
        {
            var hasText = !string.IsNullOrWhiteSpace(ExtractedText);
            var hasTable = HasTableData && TablePreview != null;

            if (!hasText && !hasTable)
                return;

            if (hasText && hasTable)
            {
                var content = new StringBuilder();
                content.AppendLine(ExtractedText.Trim());
                content.AppendLine();
                content.Append(BuildClipboardTableText(TablePreview!));
                Clipboard.SetText(content.ToString());
                StatusMessage = "Text and table data copied to clipboard!";
                return;
            }

            if (hasTable)
            {
                Clipboard.SetText(BuildClipboardTableText(TablePreview!));
                StatusMessage = "Table data copied to clipboard!";
                return;
            }

            Clipboard.SetText(ExtractedText);
            StatusMessage = "Text copied to clipboard!";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to copy text: {ex.Message}";
        }
    }

    private static string BuildClipboardTableText(DataView tableView)
    {
        if (tableView.Table == null || tableView.Table.Columns.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        var columns = tableView.Table.Columns.Cast<DataColumn>().ToList();

        sb.AppendLine(string.Join("\t", columns.Select(c => c.ColumnName)));

        foreach (DataRowView rowView in tableView)
        {
            var values = columns
                .Select(c => rowView.Row[c]?.ToString()?.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ") ?? string.Empty);
            sb.AppendLine(string.Join("\t", values));
        }

        return sb.ToString();
    }

    #endregion

    #region CanExecute Methods

    private bool CanImportImage()
    {
        return !IsProcessing;
    }

    private bool CanClear()
    {
        return SelectedImage != null || !string.IsNullOrEmpty(ExtractedText);
    }

    private bool CanCopyText()
    {
        return (!string.IsNullOrEmpty(ExtractedText) || HasTableData) && !IsProcessing;
    }

    #endregion
}