using System.Collections.Specialized;
using GodMode.Maui.ViewModels;

namespace GodMode.Maui.Views;

public partial class ProjectPage : ContentPage
{
    private bool _isInitialLoad = true;

    public ProjectPage(ProjectViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;

        viewModel.OutputMessages.CollectionChanged += OnOutputMessagesChanged;
    }

    private void OnOutputMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (BindingContext is not ProjectViewModel vm) return;

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Reset:
                // Collection was cleared - next add is initial load
                _isInitialLoad = true;
                break;

            case NotifyCollectionChangedAction.Add when _isInitialLoad:
                // First message after clear - scroll to bottom once
                _isInitialLoad = false;
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    await Task.Delay(50);
                    if (vm.OutputMessages.Count > 0)
                    {
                        OutputCollectionView.ScrollTo(vm.OutputMessages.Count - 1, position: ScrollToPosition.End, animate: false);
                    }
                });
                break;
        }
        // ItemsUpdatingScrollMode="KeepLastItemInView" handles subsequent adds
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
