using System.Collections.Specialized;
using GodMode.Maui.ViewModels;

namespace GodMode.Maui.Views;

public partial class ProjectPage : ContentPage
{
    public ProjectPage(ProjectViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;

        // Subscribe to collection changes to auto-scroll to bottom
        viewModel.OutputMessages.CollectionChanged += OnOutputMessagesChanged;
    }

    private void OnOutputMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && BindingContext is ProjectViewModel vm)
        {
            // Scroll to the last item
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                // Small delay to let the UI render the new item
                await Task.Delay(50);
                if (vm.OutputMessages.Count > 0)
                {
                    OutputCollectionView.ScrollTo(vm.OutputMessages.Count - 1, position: ScrollToPosition.End, animate: false);
                }
            });
        }
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
            viewModel.OutputMessages.CollectionChanged -= OnOutputMessagesChanged;
            viewModel.Dispose();
        }
    }

    private async void OnInputEditorCompleted(object? sender, EventArgs e)
    {
        if (BindingContext is ProjectViewModel viewModel && viewModel.SendInputCommand.CanExecute(null))
        {
            await viewModel.SendInputCommand.ExecuteAsync(null);
        }
    }
}
