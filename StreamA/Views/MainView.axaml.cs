using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Splat;
using StreamA.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;

namespace StreamA.Views;
public partial class MainView : UserControl
{
    private readonly ICameraProvider? _cameraProvider;
    private readonly ICameraService? _cameraService;
    private ICodeRecognizeService? _codeRecognizeService;

    private Image _image;
    private IList<BarcodeOverlay> _overlays;

    private IDisposable? _cameraSubscription;
    private IDisposable? _codeDetectedSubscription;
    private string _host = "127.0.0.1";
    private int _port = 12345;

    public MainView()
    {
        InitializeComponent();

        _image = new Image();// Инициализация Image
        _overlays = [];

        CameraHost.Children.Add(new TextBlock { Text = "Camera not started." });

        // получение сервиса и подписка на кадры камеры
        _cameraService = Locator.Current.GetService<ICameraService>();
        _cameraSubscription = _cameraService?.Frames.Subscribe(bitmap =>
            Dispatcher.UIThread.Post(() =>// Обновление изображения на UI-потоке(использовать для View, для ViewModel -биндинг)
            {
                if (_image != null)
                    _image.Source = bitmap;
            }));

        //создание провайдера и подписка на статус камеры
        _cameraProvider = Locator.Current.GetService<ICameraProvider>();
        if (_cameraProvider is IStatusProvider statusProvider)
            statusProvider.StatusChanged += StatusChangedHandler;
    }

    public void TapedChoiceCode(CodeRecognizeService.YoloDetection detection) =>
        _cameraService?.Frames
            .Take(1) // берём только один кадр и отписываемся от дальнейших кадров
            .Subscribe(bitmap =>
            {
                var code = _codeRecognizeService?.CodeRecognize(bitmap, detection.Bound);// Распознаем код из Bitmap
                if (code is not null)
                    Dispatcher.UIThread.Invoke(() =>
                    {
                        _codeDetectedSubscription?.Dispose();//отписка от сервиса распознавания кода
                        _codeRecognizeService?.Dispose();//освобождение сервиса распознавания кода

                        _cameraProvider?.Stop();//sender stop
                        _cameraService?.Stop();//listener stop

                        _overlays.Clear();//очистка списка оверлеев
                        CameraHost.Children.Clear();//очистка контейнера

                        CameraHost.Children.Add(new TextBlock { Text = $"{code.Text}" });
                        StatusText.Text = "Отсканировано";
                    });
            });

    // обработка подписки детектированных кодов
    private void CodeDetectedSubscription(CodeRecognizeService.YoloDetection[]? results) =>
        Dispatcher.UIThread.Invoke(() =>
        {
            foreach (var overlay in _overlays)
                overlay.IsVisible = false;

            if (results != null && results.Length > 0)
            {
                var imBound = _image!.Bounds;
                var bmSize = _image?.Source?.Size;
                var scale = bmSize.HasValue ? imBound.Width / bmSize.Value.Width : 1;// вычисление масштаба для корректного отображения
                // Обработка результатов распознавания кода
                for (int i = 0; i < results.Length; i++)
                {
                    BarcodeOverlay? overlay = i < _overlays.Count ? _overlays[i] : null;
                    if (overlay is null)
                    {
                        overlay = new() { OnTapped = TapedChoiceCode };
                        _overlays.Add(overlay);
                        CameraHost.Children.Add(overlay);
                    }

                    overlay.YoloDetection = results[i];//original rectangle coordinates
                    overlay.Width = results[i].Bound.Width * scale;
                    overlay.Height = results[i].Bound.Height * scale;
                    overlay.IsVisible = true;

                    Canvas.SetLeft(overlay, imBound.Left + results[i].Bound.X * scale);
                    Canvas.SetTop(overlay, imBound.Top + results[i].Bound.Y * scale);
                }
            }
        });
    private void OnStartClick(object? sender, RoutedEventArgs e)
    {
        if (_cameraProvider?.IsWorked == true && _cameraService?.IsWorked == true)
            return;

        // сервис распознавания кода
        _codeRecognizeService = Locator.Current.GetService<ICodeRecognizeService>();
        _codeDetectedSubscription = _codeRecognizeService?.Codes.Subscribe(onNext: CodeDetectedSubscription);

        if (_image != null)
        {
            _image.Width = CameraHost.Bounds.Width;
            _image.Height = CameraHost.Bounds.Height;
            CameraHost.Children.Add(_image);
        }

        StatusText.Text = "Запуск камеры...";
        RetryButton.IsVisible = false;

        if (_cameraProvider != null && _cameraService != null)
        {
            _cameraService.Start(_host, _port);//listener start
            _cameraProvider.Start(_host, _port);//sender start
        }
        else
            CameraHost.Children.Add(new TextBlock { Text = "Camera  not available." });
    }

    private void OnStopClick(object? sender, RoutedEventArgs e)
    {
        if (_cameraProvider?.IsWorked == false && _cameraService?.IsWorked == false)
            return;

        _codeDetectedSubscription?.Dispose();//отписка от сервиса распознавания кода
        _codeRecognizeService?.Dispose();//освобождение сервиса распознавания кода

        _cameraProvider?.Stop();//sender stop
        _cameraService?.Stop();//listener stop

        _overlays.Clear();//очистка списка оверлеев
        CameraHost.Children.Clear();//очистка контейнера

        CameraHost.Children.Add(new TextBlock { Text = "Camera stoped." });
        StatusText.Text = "Остановлено";
        RetryButton.IsVisible = false;
    }

    private void OnSwitchCameraClick(object? sender, RoutedEventArgs e)
    {
        if (_cameraProvider is not null)
        {
            _cameraProvider.SwitchCamera();
            StatusText.Text = "Переключение камеры...";
        }
    }

    private void OnRetryClick(object? sender, RoutedEventArgs e)
    {
        StatusText.Text = "Повторная попытка...";
        RetryButton.IsVisible = false;
        OnStartClick(sender, e);
    }

    // обработка события статуса камеры
    private void StatusChangedHandler(string status) => Dispatcher.UIThread.Post(() =>
    {
        StatusText.Text = status;
        RetryButton.IsVisible = status.Contains("Ошибка") || status.Contains("Потеряно соединение");
    });

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        _codeDetectedSubscription?.Dispose();
        _codeRecognizeService?.Dispose();

        _cameraSubscription?.Dispose();
        _cameraService?.Dispose();
        if (_cameraProvider is IStatusProvider statusProvider)
            statusProvider.StatusChanged -= StatusChangedHandler;
    }
}
