using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Aimmy2
{
    public partial class StartupWindow : Window
    {
        public StartupWindow()
        {
            InitializeComponent();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Generate particles when window is loaded and has dimensions
            await GenerateParticlesAsync();
        }

        public async Task AnimateStartup()
        {
            // Reveal window animation
            var revealAnimation = new RectAnimation(new Rect(0, 0, this.Width, this.Height), new Duration(TimeSpan.FromSeconds(0.8)))
            {
                EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
            };
            if(RevealClip != null)
            {
                RevealClip.BeginAnimation(RectangleGeometry.RectProperty, revealAnimation);
            }
            
            // Start all the storyboards for the animations
            (Resources["LogoRevealAnimation"] as Storyboard)?.Begin();
            await Task.Delay(400);
            (Resources["TextRevealAnimation"] as Storyboard)?.Begin();
            (Resources["PulseAnimation"] as Storyboard)?.Begin();
            (Resources["LoadingDotsAnimation"] as Storyboard)?.Begin();

            // Let the animations run for a bit
            await Task.Delay(8000);
        }

        public async void AnimateTransitionAndClose(Window newWindow)
        {
            // Set the new window as the main window of the application
            Application.Current.MainWindow = newWindow;

            newWindow.Opacity = 0;
            newWindow.Show();
            
            // Find the storyboard from the resources to fade out this window
            var fadeOutStoryboard = (Storyboard)this.Resources["FadeOutAndBlur"];

            // Create an animation to fade the main window in
            var fadeInAnimation = new DoubleAnimation
            {
                To = 1,
                Duration = TimeSpan.FromSeconds(0.8),
                BeginTime = TimeSpan.FromSeconds(0.2) // Start shortly after the fade-out begins
            };

            var tcs = new TaskCompletionSource<bool>();
            if (fadeOutStoryboard != null)
            {
                fadeOutStoryboard.Completed += (s, _) => tcs.SetResult(true);

                // Begin all animations
                fadeOutStoryboard.Begin(this);
                newWindow.BeginAnimation(Window.OpacityProperty, fadeInAnimation);

                // Wait for the fade-out to complete
                await tcs.Task;
            }
            else
            {
                // If storyboard fails, just fade in the new window
                newWindow.Opacity = 1;
            }

            this.Close();
        }

        private async Task GenerateParticlesAsync()
        {
            var random = new Random();
            if (ParticleCanvas.ActualWidth > 0 && ParticleCanvas.ActualHeight > 0)
            {
                for (int i = 0; i < 50; i++)
                {
                    var particle = new Ellipse
                    {
                        Width = random.Next(2, 5),
                        Height = random.Next(2, 5),
                        Fill = new SolidColorBrush(Color.FromArgb((byte)random.Next(50, 150), 255, 165, 0)), // Theme-colored particle (Orange)
                        Opacity = 0,
                    };

                    Canvas.SetLeft(particle, random.NextDouble() * ParticleCanvas.ActualWidth);
                    Canvas.SetTop(particle, random.NextDouble() * ParticleCanvas.ActualHeight);
                    ParticleCanvas.Children.Add(particle);

                    var fadeInAnimation = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromSeconds(random.NextDouble() * 1 + 0.5)))
                    {
                        BeginTime = TimeSpan.FromSeconds(random.NextDouble() * 1)
                    };
                    particle.BeginAnimation(OpacityProperty, fadeInAnimation);
                    
                    var moveAnimationY = new DoubleAnimation
                    {
                        To = Canvas.GetTop(particle) - random.Next(50, 150),
                        Duration = new Duration(TimeSpan.FromSeconds(random.Next(4, 8))),
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever
                    };
                    particle.BeginAnimation(Canvas.TopProperty, moveAnimationY);
                    
                    // Yield control to the UI thread to prevent freezing during particle generation
                    await Task.Delay(10);
                }
            }
        }
    }
}