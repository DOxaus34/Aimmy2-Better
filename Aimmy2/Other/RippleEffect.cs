using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Aimmy2.Other
{
    public class RippleEffect : ShaderEffect
    {
        private static PixelShader _pixelShader = new PixelShader { UriSource = new Uri("pack://application:,,,/TotallyNotAimmyV2;component/Other/RippleEffect.ps", UriKind.Absolute) };

        public RippleEffect()
        {
            PixelShader = _pixelShader;
            UpdateShaderValue(CenterProperty);
            UpdateShaderValue(ProgressProperty);
        }

        public static readonly DependencyProperty CenterProperty = DependencyProperty.Register("Center", typeof(Point), typeof(RippleEffect), new UIPropertyMetadata(new Point(0.5, 0.5), PixelShaderConstantCallback(1)));
        public Point Center
        {
            get { return (Point)GetValue(CenterProperty); }
            set { SetValue(CenterProperty, value); }
        }

        public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register("Progress", typeof(double), typeof(RippleEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(0)));
        public double Progress
        {
            get { return (double)GetValue(ProgressProperty); }
            set { SetValue(ProgressProperty, value); }
        }
    }
} 