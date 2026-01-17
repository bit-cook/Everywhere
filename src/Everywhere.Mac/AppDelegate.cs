using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Common;
using Everywhere.ViewModels;

namespace Everywhere.Mac;

[Register("AppDelegate")]
public class AppDelegate : NSApplicationDelegate
{
    public override bool ApplicationShouldHandleReopen(NSApplication sender, bool hasVisibleWindows)
    {
        // We handled the reopen by showing the chat window.
        WeakReferenceMessenger.Default.Send<ApplicationCommand>(new ShowWindowCommand(nameof(ChatWindowViewModel)));
        return true;
    }
}