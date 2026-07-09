using System.Windows;
using OCRReader.ViewModels;

namespace OCRReader
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            // Set DataContext to OcrViewModel for MVVM data binding
            this.DataContext = new OcrViewModel();
        }
    }
}
