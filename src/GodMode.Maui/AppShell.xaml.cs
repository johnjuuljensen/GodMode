using GodMode.Maui.Views;

namespace GodMode.Maui;

public partial class AppShell : Shell
{
	public AppShell()
	{
		InitializeComponent();

		// Register routes for navigation
		Routing.RegisterRoute("host", typeof(HostPage));
		Routing.RegisterRoute("project", typeof(ProjectPage));
		Routing.RegisterRoute("addProfile", typeof(AddProfilePage));
		Routing.RegisterRoute("addServer", typeof(AddServerPage));
		Routing.RegisterRoute("editServer", typeof(EditServerPage));
		Routing.RegisterRoute("createProject", typeof(CreateProjectPage));
	}
}
