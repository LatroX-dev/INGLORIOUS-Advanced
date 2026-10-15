using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace InGlorious.Core;

// A small shared outline vocabulary; labels remain plain text for localization and accessibility.
public static class UiIcons
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached("Kind", typeof(string), typeof(UiIcons), new PropertyMetadata(null, OnKindChanged));
    public static readonly DependencyProperty DataProperty = DependencyProperty.RegisterAttached("Data", typeof(Geometry), typeof(UiIcons));
    public static readonly DependencyProperty IconOnlyProperty = DependencyProperty.RegisterAttached("IconOnly", typeof(bool), typeof(UiIcons), new PropertyMetadata(false, (d, _) => { if (d is Button button) SetTemplate(button); }));
    public static void SetIconOnly(DependencyObject target, bool value) => target.SetValue(IconOnlyProperty, value);
    public static bool GetIconOnly(DependencyObject target) => (bool)target.GetValue(IconOnlyProperty);
    public static void SetKind(DependencyObject target, string value) => target.SetValue(KindProperty, value);
    public static string? GetKind(DependencyObject target) => (string?)target.GetValue(KindProperty);
    public static Geometry? GetData(DependencyObject target) => (Geometry?)target.GetValue(DataProperty);

    private static readonly IReadOnlyDictionary<string, string> Shapes = new Dictionary<string, string>
    {
        ["light"] = "M12,7 A5,5 0 1 1 12,17 A5,5 0 1 1 12,7 M12,1 L12,4 M12,20 L12,23 M1,12 L4,12 M20,12 L23,12 M4,4 L6,6 M18,18 L20,20 M4,20 L6,18 M18,6 L20,4",
        ["keyboard"] = "M2,5 L22,5 L22,19 L2,19 Z M5,9 L7,9 M10,9 L12,9 M15,9 L17,9 M19,9 L20,9 M5,12 L7,12 M10,12 L12,12 M15,12 L20,12 M6,16 L18,16",
        ["layers"] = "M2,7 L12,2 L22,7 L12,12 Z M2,12 L12,17 L22,12 M2,17 L12,22 L22,17",
        ["macro"] = "M3,3 L9,3 L9,9 L3,9 Z M6,9 L6,17 L13,17 M16,12 L22,17 L16,22 Z",
        ["settings"] = "M3,6 L8,6 M12,6 L21,6 M3,18 L14,18 M18,18 L21,18 M8,3 L12,3 L12,9 L8,9 Z M14,15 L18,15 L18,21 L14,21 Z",
        ["folder"] = "M2,6 L9,6 L11,9 L22,9 L22,20 L2,20 Z M2,6 L2,4 L9,4 L11,6 L20,6 L20,9",
        ["select"] = "M8,3 L3,3 L3,8 M16,3 L21,3 L21,8 M3,16 L3,21 L8,21 M16,21 L21,21 L21,16 M7,12 L10,15 L17,8",
        ["clear"] = "M8,3 L3,3 L3,8 M16,3 L21,3 L21,8 M3,16 L3,21 L8,21 M16,21 L21,21 L21,16 M8,8 L16,16 M8,16 L16,8",
        ["save"] = "M3,3 L18,3 L21,6 L21,21 L3,21 Z M7,3 L7,9 L17,9 L17,3 M7,21 L7,14 L17,14 L17,21",
        ["copy"] = "M8,8 L21,8 L21,21 L8,21 Z M16,8 L16,3 L3,3 L3,16 L8,16",
        ["delete"] = "M3,6 L21,6 M8,6 L8,3 L16,3 L16,6 M5,6 L6,21 L18,21 L19,6 M10,10 L10,17 M14,10 L14,17",
        ["import"] = "M12,2 L12,15 M7,10 L12,15 L17,10 M3,15 L3,21 L21,21 L21,15",
        ["export"] = "M12,16 L12,3 M7,8 L12,3 L17,8 M3,15 L3,21 L21,21 L21,15",
        ["palette"] = "M12,2 C6,2 2,6 2,12 C2,18 6,22 11,22 C15,22 15,19 13,17 C12,15 15,13 18,14 C22,15 23,11 21,7 C19,3 16,2 12,2 Z M7,7 L8,7 M13,5 L14,5 M18,8 L19,8 M5,12 L6,12",
        ["plus"] = "M12,3 L12,21 M3,12 L21,12",
        ["record"] = "M12,3 A9,9 0 1 1 12,21 A9,9 0 1 1 12,3 M12,8 A4,4 0 1 1 12,16 A4,4 0 1 1 12,8",
        ["check"] = "M3,12 L9,18 L21,5",
        ["refresh"] = "M20,9 A9,9 0 0 0 4,6 M20,3 L20,9 L14,9 M4,15 A9,9 0 0 0 20,18 M4,21 L4,15 L10,15",
        ["restore"] = "M3,4 L3,10 L9,10 M3,10 C5,5 9,3 14,4 C19,5 22,10 20,15 C18,21 10,23 5,18 M12,8 L12,13 L16,15",
        ["chip"] = "M6,6 L18,6 L18,18 L6,18 Z M9,1 L9,6 M15,1 L15,6 M9,18 L9,23 M15,18 L15,23 M1,9 L6,9 M1,15 L6,15 M18,9 L23,9 M18,15 L23,15",
        ["shield"] = "M12,2 L21,6 L20,14 C19,18 15,21 12,23 C9,21 5,18 4,14 L3,6 Z M7,12 L10,15 L17,8",
        ["star"] = "M12,2 L15,9 L23,9 L17,14 L19,22 L12,18 L5,22 L7,14 L1,9 L9,9 Z",
        ["action"] = "M4,3 L14,3 L14,7 M4,3 L4,21 L20,21 L20,13 M10,14 L22,2 M15,2 L22,2 L22,9",
        ["clock"] = "M12,2 A10,10 0 1 1 12,22 A10,10 0 1 1 12,2 M12,6 L12,12 L17,15",
        ["message"] = "M3,3 L21,3 L21,17 L9,17 L3,22 Z M7,8 L17,8 M7,12 L14,12"
    };

    private static void OnKindChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not Button button || e.NewValue is not string kind || !Shapes.TryGetValue(kind, out var path)) return;
        var shape = Geometry.Parse(path); shape.Freeze(); button.SetValue(DataProperty, shape);
        SetTemplate(button);
        if (button.ReadLocalValue(System.Windows.Automation.AutomationProperties.NameProperty) == DependencyProperty.UnsetValue)
            BindingOperations.SetBinding(button, System.Windows.Automation.AutomationProperties.NameProperty, new Binding("Content") { Source = button });
    }
    private static void SetTemplate(Button button) => button.SetResourceReference(ContentControl.ContentTemplateProperty, GetIconOnly(button) ? "IconOnlyButtonContent" : "IconButtonContent");
}
