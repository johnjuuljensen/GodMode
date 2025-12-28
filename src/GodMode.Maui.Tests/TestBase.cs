using NSubstitute;
using GodMode.ClientBase.Services;

namespace GodMode.Maui.Tests;

/// <summary>
/// Base class for ViewModel tests providing common mock setup
/// </summary>
public abstract class TestBase
{
    protected IProfileService ProfileService { get; }
    protected IHostConnectionService HostConnectionService { get; }
    protected IProjectService ProjectService { get; }
    protected INotificationService NotificationService { get; }

    protected TestBase()
    {
        ProfileService = Substitute.For<IProfileService>();
        HostConnectionService = Substitute.For<IHostConnectionService>();
        ProjectService = Substitute.For<IProjectService>();
        NotificationService = Substitute.For<INotificationService>();
    }
}
