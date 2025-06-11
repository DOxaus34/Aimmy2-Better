using System.Windows;

namespace Aimmy2.UILibrary
{
    /// <summary>
    /// Interaction logic for ACredit.xaml
    /// </summary>
    public partial class ACredit : System.Windows.Controls.UserControl
    {
        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register("Title", typeof(string), typeof(ACredit), new PropertyMetadata(""));

        public string Title
        {
            get { return (string)GetValue(TitleProperty); }
            set { SetValue(TitleProperty, value); }
        }

        public static readonly DependencyProperty DescriptionProperty =
            DependencyProperty.Register("Description", typeof(string), typeof(ACredit), new PropertyMetadata(""));

        public string Description
        {
            get { return (string)GetValue(DescriptionProperty); }
            set { SetValue(DescriptionProperty, value); }
        }

        public ACredit()
        {
            InitializeComponent();
            this.DataContext = this;
        }
    }
}