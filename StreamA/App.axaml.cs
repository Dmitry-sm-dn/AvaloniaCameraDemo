using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Splat;
using StreamA.Services;
using StreamA.ViewModels;
using StreamA.Views;
using System;
using System.IO;

namespace StreamA;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Locator.CurrentMutable.RegisterLazySingleton<ICameraService>(() => new CameraService());
        Locator.CurrentMutable.Register<ICodeRecognizeService>(() => new CodeRecognizeService());//transiently from fabrica

        var pathOnnx = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "yolo8v_barcode.onnx");
        if (!File.Exists(pathOnnx))
        {
            using var stream = AssetLoader.Open(new Uri($"avares://StreamA/Assets/yolo8v_barcode.onnx"));
            using var fileStream = new FileStream(pathOnnx, FileMode.Create);
            stream.CopyTo(fileStream);
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel()
            };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MainView
            {
                DataContext = new MainViewModel()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
