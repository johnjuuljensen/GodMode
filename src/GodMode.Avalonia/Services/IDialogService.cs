namespace GodMode.Avalonia.Services;

public interface IDialogService
{
	Task<bool> ConfirmAsync(string title, string message, string accept = "OK", string cancel = "Cancel");
	Task AlertAsync(string title, string message);
}
