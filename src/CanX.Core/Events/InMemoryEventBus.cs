// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;

namespace CanX.Core.Events;

public sealed class InMemoryEventBus(Action<Exception>? subscriberFailureHandler = null) : IEventBus
{
    private readonly ConcurrentDictionary<Type, SubscriptionSet> _subscriptions = new();

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler)
        where TEvent : IAppEvent
    {
        ArgumentNullException.ThrowIfNull(handler);

        var subscription = new Subscription<TEvent>(handler);
        var set = _subscriptions.GetOrAdd(typeof(TEvent), static _ => new SubscriptionSet());
        set.Add(subscription);
        return new Unsubscriber(() => set.Remove(subscription));
    }

    public void Publish<TEvent>(TEvent appEvent)
        where TEvent : IAppEvent
    {
        ArgumentNullException.ThrowIfNull(appEvent);

        if (!_subscriptions.TryGetValue(typeof(TEvent), out var set))
        {
            return;
        }

        // Publishers include engine worker threads; a failing subscriber must not abort the publisher or
        // starve the remaining subscribers.
        foreach (var subscription in set.Snapshot())
        {
            try
            {
                ((Subscription<TEvent>)subscription).Handler(appEvent);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                subscriberFailureHandler?.Invoke(exception);
            }
        }
    }

    private interface ISubscription;

    private sealed record Subscription<TEvent>(Action<TEvent> Handler) : ISubscription
        where TEvent : IAppEvent;

    private sealed class SubscriptionSet
    {
        private readonly Lock _lock = new();
        private readonly List<ISubscription> _items = [];

        public void Add(ISubscription subscription)
        {
            lock (_lock)
            {
                _items.Add(subscription);
            }
        }

        public void Remove(ISubscription subscription)
        {
            lock (_lock)
            {
                _items.Remove(subscription);
            }
        }

        public ISubscription[] Snapshot()
        {
            lock (_lock)
            {
                return [.. _items];
            }
        }
    }

    private sealed class Unsubscriber(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
