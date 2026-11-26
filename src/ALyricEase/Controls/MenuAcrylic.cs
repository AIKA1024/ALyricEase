using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace ALyricEase.Controls;

public static class MenuAcrylic
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Border, bool>("IsEnabled", typeof(MenuAcrylic));

    public static bool GetIsEnabled(Border surface) => surface.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Border surface, bool value) => surface.SetValue(IsEnabledProperty, value);

    static MenuAcrylic()
    {
        IsEnabledProperty.Changed.AddClassHandler<Border>((surface, change) =>
        {
            if (change.GetNewValue<bool>())
            {
                surface.AttachedToVisualTree += OnAttached;
                if (surface.IsAttachedToVisualTree()) Install(surface);
            }
            else
            {
                surface.AttachedToVisualTree -= OnAttached;
                if (surface.Child is MenuSurface panel)
                {
                    panel.Children.Remove(panel.Content);
                    surface.Child = panel.Content;
                }
            }
        });
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e) => Install((Border)sender!);

    private static void Install(Border surface)
    {
        if (surface.Child is not ScrollViewer scroller) return;
        surface.Child = null;
        surface.Child = new MenuSurface(surface, scroller);
    }

    // 一级菜单与子菜单沿用 Fluent 的滚动、焦点及边框模板。材质延伸到边框内缘。
    private sealed class MenuSurface : Panel
    {
        private readonly Border _surface;
        private readonly ChromeAcrylicLayer _acrylic = new();
        public ScrollViewer Content { get; }

        public MenuSurface(Border surface, ScrollViewer content)
        {
            _surface = surface;
            Content = content;
            Children.Add(_acrylic);
            Children.Add(content);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var bounds = new Rect(finalSize);
            _acrylic.Arrange(bounds.Inflate(_surface.Padding));
            Content.Arrange(bounds);
            return finalSize;
        }
    }
}
