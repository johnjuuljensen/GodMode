namespace GodMode.Maui.Views;

public partial class AddAccountPage : ContentPage
{
    public AddAccountPage(AddAccountViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
