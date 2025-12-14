using GodMode.Maui.ViewModels;

namespace GodMode.Maui.Views;

public partial class EditServerPage : ContentPage
{
    public EditServerPage(EditServerViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
