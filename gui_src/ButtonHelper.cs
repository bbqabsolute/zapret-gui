using System.Windows;
using System.Windows.Media;

namespace ZapretGUI
{
    public static class ButtonHelper
    {
        public static readonly DependencyProperty IconProperty =
            DependencyProperty.RegisterAttached(
                "Icon",
                typeof(Geometry),
                typeof(ButtonHelper),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static Geometry? GetIcon(DependencyObject obj) => (Geometry?)obj.GetValue(IconProperty);
        public static void SetIcon(DependencyObject obj, Geometry? value) => obj.SetValue(IconProperty, value);

        public static readonly DependencyProperty IconWidthProperty =
            DependencyProperty.RegisterAttached(
                "IconWidth",
                typeof(double),
                typeof(ButtonHelper),
                new FrameworkPropertyMetadata(13.0));

        public static double GetIconWidth(DependencyObject obj) => (double)obj.GetValue(IconWidthProperty);
        public static void SetIconWidth(DependencyObject obj, double value) => obj.SetValue(IconWidthProperty, value);

        public static readonly DependencyProperty IconHeightProperty =
            DependencyProperty.RegisterAttached(
                "IconHeight",
                typeof(double),
                typeof(ButtonHelper),
                new FrameworkPropertyMetadata(13.0));

        public static double GetIconHeight(DependencyObject obj) => (double)obj.GetValue(IconHeightProperty);
        public static void SetIconHeight(DependencyObject obj, double value) => obj.SetValue(IconHeightProperty, value);

        public static readonly DependencyProperty IconMarginProperty =
            DependencyProperty.RegisterAttached(
                "IconMargin",
                typeof(Thickness),
                typeof(ButtonHelper),
                new FrameworkPropertyMetadata(new Thickness(0, 0, 7, 0)));

        public static Thickness GetIconMargin(DependencyObject obj) => (Thickness)obj.GetValue(IconMarginProperty);
        public static void SetIconMargin(DependencyObject obj, Thickness value) => obj.SetValue(IconMarginProperty, value);

        public static readonly DependencyProperty CornerRadiusProperty =
            DependencyProperty.RegisterAttached(
                "CornerRadius",
                typeof(CornerRadius),
                typeof(ButtonHelper),
                new FrameworkPropertyMetadata(new CornerRadius(7), FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static CornerRadius GetCornerRadius(DependencyObject obj) => (CornerRadius)obj.GetValue(CornerRadiusProperty);
        public static void SetCornerRadius(DependencyObject obj, CornerRadius value) => obj.SetValue(CornerRadiusProperty, value);
    }
}
