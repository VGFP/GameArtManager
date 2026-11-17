using System;
using GameArtManager.Widgets;
using Microsoft.Gaming.XboxGameBar;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.Core;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace GameArtManager
{
    /// <summary>Application entry: hosts the Game Bar widget and the full-window UI.</summary>
    sealed partial class App : Application
    {
        private XboxGameBarWidget _primaryWidget;

        public App()
        {
            InitializeComponent();
            // Gamepad pointer mode is an Xbox-device-family feature only — Windows
            // ignores it, so on PCs (handhelds / full screen experience) the pages
            // translate VirtualKey.Gamepad* events themselves (Core.GamepadFocus).
            // Keep Auto so a real Xbox console still gets its system behaviour.
            RequiresPointerMode = Windows.UI.Xaml.ApplicationRequiresPointerMode.Auto;
            Suspending += OnSuspending;
            UnhandledException += OnUnhandledException;
        }

        private void OnUnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            try
            {
                var message = $"EXCEPTION: {e.Message}\n--- {e.Exception?.GetType().FullName}\n{e.Exception?.StackTrace}";
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "crash.txt"),
                    message);
            }
            catch
            {
                // diagnostics only
            }
        }

        protected override void OnActivated(IActivatedEventArgs args)
        {
            if (args.Kind == ActivationKind.Protocol)
            {
                var uri = ((IProtocolActivatedEventArgs)args).Uri;

                if (uri.Scheme.Equals("ms-gamebarwidget"))
                {
                    var widgetArgs = args as XboxGameBarWidgetActivatedEventArgs;
                    if (widgetArgs.IsLaunchActivation)
                    {
                        // New CoreWindow: bootstrap the widget connection with Game Bar
                        var rootFrame = new Frame();
                        rootFrame.NavigationFailed += OnNavigationFailed;
                        Window.Current.Content = rootFrame;

                        _primaryWidget = new XboxGameBarWidget(widgetArgs, Window.Current.CoreWindow, rootFrame);
                        rootFrame.Navigate(typeof(PrimaryWidget));

                        Window.Current.Closed += PrimaryWidgetWindow_Closed;
                        Window.Current.Activate();
                    }
                    return;
                }

                if (uri.Scheme.Equals("gameart"))
                {
                    ShowMainWindow();
                    return;
                }
            }
        }

        /// <summary>Open the full-window UI (used for FileOpenPicker etc.).</summary>
        private async void ShowMainWindow()
        {
            if (Window.Current.Content == null)
            {
                // cold start via protocol: use this window
                var frame = new Frame();
                frame.NavigationFailed += OnNavigationFailed;
                frame.Navigate(typeof(MainPage));
                Window.Current.Content = frame;
                Window.Current.Activate();
                return;
            }

            // widget session is running: open the main UI in a separate view
            var viewId = 0;
            await CoreApplication.CreateNewView().Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                var frame = new Frame();
                frame.NavigationFailed += OnNavigationFailed;
                frame.Navigate(typeof(MainPage));
                Window.Current.Content = frame;
                Window.Current.Activate();
                viewId = ApplicationView.GetApplicationViewIdForWindow(CoreApplication.GetCurrentView().CoreWindow);
            });
            if (viewId != 0)
                await ApplicationViewSwitcher.TryShowAsStandaloneAsync(viewId);
        }

        private void PrimaryWidgetWindow_Closed(object sender, CoreWindowEventArgs e)
        {
            _primaryWidget = null;
            Window.Current.Closed -= PrimaryWidgetWindow_Closed;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            var rootFrame = Window.Current.Content as Frame;

            if (rootFrame == null)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                Window.Current.Content = rootFrame;
            }

            if (e.PrelaunchActivated == false)
            {
                if (rootFrame.Content == null)
                    rootFrame.Navigate(typeof(MainPage), e.Arguments);
                Window.Current.Activate();
            }
        }

        private void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            _primaryWidget = null;
            deferral.Complete();
        }
    }
}
