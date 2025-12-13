using GodMode.Maui.ViewModels;

namespace GodMode.Maui.Views;

public partial class CreateProjectPage : ContentPage
{
    public CreateProjectPage(CreateProjectViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
