using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Headless;

internal static class DialogInputPaneProbe
{
    public static void Run(MainViewModel main)
    {
        main.Settings.ProxyModeIndex = 2;
        foreach (var size in new[] { new Size(320, 640), new Size(640, 320) })
        {
            Verify(new ProxySettingsDialogView { DataContext = main }, size);
            Verify(new RenamePlaylistDialogView { DataContext = main }, size);
        }
    }

    private static void Verify(UserControl dialog, Size size)
    {
        // Headless has no system keyboard. Apply its final inset to the same decorator as the Android host.
        var host = new InputPaneAwareDecorator { Behavior = InputPaneAwareBehavior.Resize, Child = dialog };
        var window = new Window { Width = size.Width, Height = size.Height, Content = host };
        window.Show();
        Layout(window);
        var card = dialog.FindControl<Border>("DialogCard")!;
        var initial = BoundsIn(card, window);
        var field = dialog is ProxySettingsDialogView
            ? dialog.GetVisualDescendants().OfType<TextBox>().Single(t => t.PasswordChar != default)
            : dialog.FindControl<TextBox>("NameBox")!;
        if (!field.Focus())
            throw new InvalidOperationException($"Cannot focus input in {dialog.GetType().Name}");
        Layout(window);
        var top = size.Width < size.Height ? 320 : 144;
        host.Padding = new Thickness(0, 0, 0, size.Height - top);
        Layout(window);

        var viewport = new Rect(0, 0, size.Width, top);
        var fieldBounds = BoundsIn(field, window);
        var scroll = field.GetVisualAncestors().OfType<ScrollContentPresenter>().First();
        var scrollBounds = BoundsIn(scroll, window);
        if (!viewport.Contains(BoundsIn(card, window)) || !scrollBounds.Contains(fieldBounds))
            throw new InvalidOperationException($"{dialog.GetType().Name} viewport={viewport} card={BoundsIn(card, window)} " +
                $"field={fieldBounds} scroll={scrollBounds}");
        var actions = dialog.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Content is "保存" or "取消" or "测试连接").ToArray();
        if (actions.Length != (dialog is ProxySettingsDialogView ? 3 : 2))
            throw new InvalidOperationException("Missing dialog actions");
        foreach (var button in actions)
            if (!button.IsEffectivelyVisible || !viewport.Contains(BoundsIn(button, window)))
                throw new InvalidOperationException($"{button.Content} outside {viewport}");

        host.Padding = default;
        Layout(window);
        if (BoundsIn(card, window) != initial || dialog.Bounds.Size != size)
            throw new InvalidOperationException($"{dialog.GetType().Name} failed to restore {initial}");
        Console.WriteLine($"[inputpane] PASS {dialog.GetType().Name} {size} visibleHeight={top}");
        window.Close();
    }

    private static Rect BoundsIn(Control control, Window window) =>
        new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);

    private static void Layout(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.UpdateLayout();
        }
    }
}
