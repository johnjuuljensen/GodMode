using GodMode.Maui.ViewModels;

namespace GodMode.Maui.Views;

public partial class ProjectPage : ContentPage
{
    public ProjectPage(ProjectViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (BindingContext is ProjectViewModel viewModel)
        {
            await viewModel.LoadCommand.ExecuteAsync(null);
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        if (BindingContext is ProjectViewModel viewModel)
        {
            viewModel.Dispose();
        }
    }
}
