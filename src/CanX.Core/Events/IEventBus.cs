// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Core.Events;

public interface IEventBus
{
    IDisposable Subscribe<TEvent>(Action<TEvent> handler)
        where TEvent : IAppEvent;

    void Publish<TEvent>(TEvent appEvent)
        where TEvent : IAppEvent;
}
