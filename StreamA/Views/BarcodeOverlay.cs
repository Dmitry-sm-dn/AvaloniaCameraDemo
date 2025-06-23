using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using StreamA.Services;
using System;

namespace StreamA.Views;
public class BarcodeOverlay : ContentControl
{
    public Action<CodeRecognizeService.YoloDetection>? OnTapped { get; set; } // Action to handle tap events
    public CodeRecognizeService.YoloDetection YoloDetection { get; set; } = null!;// Detected barcode information
    public BarcodeOverlay()
    {
        this.IsVisible = false; // Initially hidden

        var rectangle = new Rectangle
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            Stroke = Brushes.Red,
            StrokeThickness = 2,
            Fill = Brushes.Transparent,
        };
        rectangle.Tapped += (s, e) => this.OnTapped?.Invoke(this.YoloDetection);

        this.Content = rectangle;
    }
}