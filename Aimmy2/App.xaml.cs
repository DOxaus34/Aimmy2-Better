using Aimmy2.Class;
using Aimmy2.InputLogic;
using Aimmy2.MouseMovementLibraries.GHubSupport;
using Aimmy2.MouseMovementLibraries.RazerSupport;
using Aimmy2.Other;
using Aimmy2.UILibrary;
using Aimmy2.WinformsReplacement;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;
using UILibrary;
using Visuality;
using Application = System.Windows.Application;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Aimmy2
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Set shutdown mode to prevent app from closing when startup window closes
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Create and show startup window
            var startupWindow = new StartupWindow();
            startupWindow.Show();

            // The animation task can start immediately on the UI thread.
            Task animationTask = startupWindow.AnimateStartup();
            
            // The loading of the main window is encapsulated in its own async method.
            Task<MainWindow> loadingTask = LoadMainWindowAsync();

            // Wait for BOTH the minimum animation time AND the preloading to complete.
            await Task.WhenAll(animationTask, loadingTask);

            // Once both are done, retrieve the loaded window and transition
            var mainWindow = await loadingTask;
            startupWindow.AnimateTransitionAndClose(mainWindow);
        }

        private async Task<MainWindow> LoadMainWindowAsync()
        {
            // Yield the UI thread for a moment. This is the CRUCIAL step.
            // It allows the StartupWindow animations to begin rendering before
            // we block the thread with the MainWindow constructor.
            await Task.Delay(25);

            // Now, create the main window on the UI thread. This will still cause a stutter,
            // but it happens *after* the initial animations have started, not a dead freeze.
            var mainWindow = new MainWindow
            {
                Opacity = 0
            };

            // Asynchronously preload all data. PreloadAsync is designed to be non-blocking.
            await mainWindow.PreloadAsync();

            return mainWindow;
        }

        private void InitializeTheme()
        {
            try
            {
                // This is a placeholder implementation.
                var colorState = new Dictionary<string, dynamic>
                {
                    { "Theme Color", "#FF722ED1" }
                };
                
                SaveDictionary.LoadJSON(colorState, "bin\\colors.cfg");
                
                if (colorState.TryGetValue("Theme Color", out var themeColor) && themeColor is string colorString)
                {
                    Aimmy2.Other.ThemeManager.SetThemeColor(colorString);
                }
                else
                {
                    Aimmy2.Other.ThemeManager.SetThemeColor("#FF722ED1");
                }
            }
            catch (Exception)
            {
                Aimmy2.Other.ThemeManager.SetThemeColor("#FF722ED1");
            }
        }
    }
}