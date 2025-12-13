using GodMode.Maui.ViewModels;

namespace GodMode.Maui.Views;

public partial class AddProfilePage : ContentPage
{
    public AddProfilePage(AddProfileViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
