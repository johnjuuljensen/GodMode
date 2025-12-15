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

        // Set up Ctrl+Enter handler for the input editor
        SetupInputEditorKeyHandler();
    }

    private void SetupInputEditorKeyHandler()
    {
#if WINDOWS
        InputEditor.HandlerChanged += (s, e) =>
        {
            if (InputEditor.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.RichEditBox richEditBox)
            {
                richEditBox.PreviewKeyDown += OnInputEditorPreviewKeyDown;
            }
        };
#endif
    }

#if WINDOWS
    private void OnInputEditorPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
            var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);

            bool ctrlDown = ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            bool shiftDown = shift.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            if (ctrlDown || shiftDown)
            {
                e.Handled = true;
                if (BindingContext is ProjectViewModel vm && vm.SendInputCommand.CanExecute(null))
                {
                    MainThread.BeginInvokeOnMainThread(async () =>
                    {
                        await vm.SendInputCommand.ExecuteAsync(null);
                    });
                }
            }
        }
    }
#endif

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
}
