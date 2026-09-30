namespace TerminalV.Mirror;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
        // SoftInput on every edge still pads the cutout. That pad is the top
        // in portrait and the left in landscape. Only the keyboard edge should move.
        SafeAreaEdges = new SafeAreaEdges(
            SafeAreaRegions.None,
            SafeAreaRegions.None,
            SafeAreaRegions.None,
            SafeAreaRegions.SoftInput);
    }
}
