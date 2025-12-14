namespace GodMode.Maui.Views;

public partial class AddServerPage : ContentPage
{
    public AddServerPage(AddServerViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
