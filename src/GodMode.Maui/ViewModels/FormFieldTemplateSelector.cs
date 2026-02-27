using GodMode.ClientBase.Models;

namespace GodMode.Maui.ViewModels;

/// <summary>
/// Selects the appropriate DataTemplate based on FormField type.
/// </summary>
public class FormFieldTemplateSelector : DataTemplateSelector
{
    public DataTemplate? StringTemplate { get; set; }
    public DataTemplate? MultilineTemplate { get; set; }
    public DataTemplate? BooleanTemplate { get; set; }

    protected override DataTemplate OnSelectTemplate(object item, BindableObject container)
    {
        if (item is FormField field)
        {
            if (field.FieldType == "boolean")
                return BooleanTemplate!;
            if (field.IsMultiline)
                return MultilineTemplate!;
        }

        return StringTemplate!;
    }
}
