using GodMode.Maui.ViewModels;

namespace GodMode.Maui.Views;

public partial class HostPage : ContentPage
{
    public HostPage(HostViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (BindingContext is HostViewModel viewModel)
        {
            await viewModel.LoadCommand.ExecuteAsync(null);
        }
    }
}
