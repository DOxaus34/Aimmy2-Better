using Aimmy2.Class;
using System.Windows;

namespace Aimmy2.UILibrary
{
    /// <summary>
    /// Interaction logic for ATitle.xaml
    /// </summary>
    public partial class ATitle : System.Windows.Controls.UserControl
    {
        public static readonly DependencyProperty TitleTextProperty = DependencyProperty.Register(
            "TitleText", typeof(string), typeof(ATitle), new PropertyMetadata("Default Title", OnTitleTextPropertyChanged));

        public string TitleText
        {
            get => (string)GetValue(TitleTextProperty);
            set => SetValue(TitleTextProperty, value);
        }

        private static void OnTitleTextPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((ATitle)d).LabelTitle.Content = e.NewValue;
        }

        public ATitle()
        {
            InitializeComponent();
        }

        public ATitle(string Text, bool MinimizableMenu = false)
        {
            InitializeComponent();

            LabelTitle.Content = Text;

            if (MinimizableMenu)
            {
                Minimize.Visibility = System.Windows.Visibility.Visible;
                switch (Dictionary.minimizeState[Text])
                {
                    case false:
                        Minimize.Content = "\xE921";
                        break;

                    case true:
                        Minimize.Content = "\xE710";
                        break;
                }
            }

            Minimize.Click += (s, e) =>
            {
                //Debug.WriteLine(Minimize.Content);
                switch (Dictionary.minimizeState[Text])
                {
                    case false:
                        Minimize.Content = "\xE710";
                        break;

                    case true:
                        Minimize.Content = "\xE921";
                        break;
                }

                Dictionary.minimizeState[Text] = !Dictionary.minimizeState[Text];
            };
        }
    }
}