using Android;
using Android.App;
using Android.Content.PM;
using Android.Content.Res;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using Avalonia;
using Avalonia.Android;
using Avalonia.ReactiveUI;
using Splat;
using StreamA.Services;

namespace StreamA.Android;

[Activity(
    Label = "StreamA.Android",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .UseReactiveUI();
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        Locator.CurrentMutable.RegisterLazySingleton<ICameraProvider>(() => new AndroidCameraProvider(this));//platform-specific camera provider registration

        base.OnCreate(savedInstanceState);

        if (ContextCompat.CheckSelfPermission(this, Manifest.Permission.Camera) != Permission.Granted) // Corrected reference to Manifest
        {
            ActivityCompat.RequestPermissions(this, [Manifest.Permission.Camera], 0);
        }
    }

    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);

        // Notify the camera provider about configuration changes
        Locator.Current.GetService<ICameraProvider>()?.ConfigurationChanged();
    }
}
