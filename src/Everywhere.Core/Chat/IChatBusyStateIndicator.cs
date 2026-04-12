using System;
using Everywhere.Common;

namespace Everywhere.Chat;

/// <summary>
/// Defines a contract for indicating busy state in a chat context.
/// </summary>
public interface IChatBusyStateIndicator
{
    /// <summary>
    /// Sets a busy message to indicate that an operation is in progress.
    /// </summary>
    /// <param name="busyMessage">The localized message to display.</param>
    /// <returns>An <see cref="IDisposable"/> that, when disposed, clears the busy state.</returns>
    IDisposable SetBusyMessage(IDynamicResourceKey? busyMessage);
}
