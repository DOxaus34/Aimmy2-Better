using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace Aimmy2
{
    public partial class SplashScreen : Window
    {
        public SplashScreen()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var startupAnimation = (Storyboard)FindResource("StartupAnimation");
            startupAnimation.Completed += (s, _) =>
            {
                var mainWindow = new MainWindow();
                mainWindow.Opacity = 0;

                mainWindow.ContentRendered += (s2, e2) =>
                {
                    var fadeInAnimation = (Storyboard)mainWindow.FindResource("FadeInAnimation");
                    fadeInAnimation.Begin();

                    var fadeOutAnimation = (Storyboard)this.FindResource("FadeOutAnimation");
                    fadeOutAnimation.Completed += (s3, e3) =>
                    {
                        this.Close();
                    };
                    fadeOutAnimation.Begin();
                };

                mainWindow.Show();
            };
            startupAnimation.Begin();
        }
    }
} 