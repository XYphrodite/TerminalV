using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;

namespace TerminalV.Mobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        HideSystemBars();
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus) HideSystemBars();
    }

    private void HideSystemBars()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
        {
            Window?.SetDecorFitsSystemWindows(false);
            if (Window != null)
            {
                Window.Attributes!.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
                Window.SetStatusBarColor(Android.Graphics.Color.Transparent);
                Window.SetNavigationBarColor(Android.Graphics.Color.Transparent);
            }
            var controller = Window?.InsetsController;
            if (controller != null)
            {
                controller.Hide(WindowInsets.Type.StatusBars() | WindowInsets.Type.NavigationBars());
                controller.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
            }
        }
        else
        {
#pragma warning disable CA1416
            Window?.AddFlags(WindowManagerFlags.Fullscreen);
            Window?.ClearFlags(WindowManagerFlags.ForceNotFullscreen);
            Window?.DecorView.SystemUiVisibility = (StatusBarVisibility)(
                (int)SystemUiFlags.Fullscreen |
                (int)SystemUiFlags.HideNavigation |
                (int)SystemUiFlags.ImmersiveSticky |
                (int)SystemUiFlags.LayoutFullscreen |
                (int)SystemUiFlags.LayoutStable);
#pragma warning restore CA1416
        }
    }
}
