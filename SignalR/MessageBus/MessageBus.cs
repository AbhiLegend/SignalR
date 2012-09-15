﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using SignalR.Infrastructure;

namespace SignalR
{
    /// <summary>
    /// 
    /// </summary>
    public class MessageBus : IMessageBus, IDisposable
    {
        protected readonly ConcurrentDictionary<string, Topic> _topics = new ConcurrentDictionary<string, Topic>();
        private readonly MessageBroker _broker;

        private const int DefaultMessageStoreSize = 5000;

        private readonly ITraceManager _trace;

        protected readonly IPerformanceCounterWriter _counters;
        private readonly PerformanceCounter _msgsTotalCounter;
        private readonly PerformanceCounter _msgsPerSecCounter;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="resolver"></param>
        public MessageBus(IDependencyResolver resolver)
            : this(resolver.Resolve<ITraceManager>(), resolver.Resolve<IPerformanceCounterWriter>())
        {
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="traceManager"></param>
        public MessageBus(ITraceManager traceManager, IPerformanceCounterWriter performanceCounterWriter)
        {
            _trace = traceManager;
            
            _counters = performanceCounterWriter;
            _msgsTotalCounter = _counters.GetCounter(PerformanceCounters.MessageBusMessagesPublishedTotal);
            _msgsPerSecCounter = _counters.GetCounter(PerformanceCounters.MessageBusMessagesPublishedPerSec);

            _broker = new MessageBroker(_topics, _counters)
            {
                Trace = Trace
            };
        }

        private TraceSource Trace
        {
            get
            {
                return _trace["SignalR.MessageBus"];
            }
        }

        public int AllocatedWorkers
        {
            get
            {
                return _broker.AllocatedWorkers;
            }
        }

        public int BusyWorkers
        {
            get
            {
                return _broker.BusyWorkers;
            }
        }

        /// <summary>
        /// Publishes a new message to the specified event on the bus.
        /// </summary>
        /// <param name="source">A value representing the source of the data sent.</param>
        public virtual Task Publish(Message message)
        {
            Topic topic = GetTopic(message.Key);

            topic.Store.Add(message);

            ScheduleTopic(topic);

            return TaskAsyncHelper.Empty;
        }
        
        protected ulong Save(Message message)
        {
            Topic topic = GetTopic(message.Key);

            return topic.Store.Add(message);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="subscriber"></param>
        /// <param name="cursor"></param>
        /// <param name="callback"></param>
        /// <returns></returns>
        public virtual IDisposable Subscribe(ISubscriber subscriber, string cursor, Func<MessageResult, Task<bool>> callback, int messageBufferSize)
        {
            Subscription subscription = CreateSubscription(subscriber, cursor, callback, messageBufferSize);

            var topics = new HashSet<Topic>();

            foreach (var key in subscriber.EventKeys)
            {
                Topic topic = GetTopic(key);

                // Set the subscription for this topic
                subscription.SetEventTopic(key, topic);

                // Add it to the list of topics
                topics.Add(topic);
            }

            foreach (var topic in topics)
            {
                topic.AddSubscription(subscription);
            }

            Action<string> eventAdded = eventKey =>
            {
                Topic topic = GetTopic(eventKey);

                // Add or update the cursor (in case it already exists)
                subscription.AddEvent(eventKey, topic);

                // Add it to the list of subs
                topic.AddSubscription(subscription);
            };

            Action<string> eventRemoved = eventKey => RemoveEvent(subscription, eventKey);

            subscriber.EventAdded += eventAdded;
            subscriber.EventRemoved += eventRemoved;

            // If there's a cursor then schedule work for this subscription
            if (!String.IsNullOrEmpty(cursor))
            {
                _broker.Schedule(subscription);
            }

            return new DisposableAction(() =>
            {
                // This will stop work from continuting to happen
                subscription.Dispose();

                subscriber.EventAdded -= eventAdded;
                subscriber.EventRemoved -= eventRemoved;

                string currentCursor = subscription.GetCursor();

                foreach (var eventKey in subscriber.EventKeys)
                {
                    RemoveEvent(subscription, eventKey);
                }

                subscription.Invoke(new MessageResult(currentCursor));
            });
        }

        protected virtual Subscription CreateSubscription(ISubscriber subscriber, string cursor, Func<MessageResult, Task<bool>> callback, int messageBufferSize)
        {
            return new DefaultSubscription(subscriber.Identity, subscriber.EventKeys, _topics, cursor, callback, messageBufferSize, _counters);
        }

        protected void ScheduleEvent(string eventKey)
        {
            Topic topic;
            if (_topics.TryGetValue(eventKey, out topic))
            {
                ScheduleTopic(topic);
            }
        }

        private void ScheduleTopic(Topic topic)
        {
            try
            {
                topic.SubscriptionLock.EnterReadLock();

                for (int i = 0; i < topic.Subscriptions.Count; i++)
                {
                    ISubscription subscription = topic.Subscriptions[i];
                    _broker.Schedule(subscription);
                }
            }
            finally
            {
                topic.SubscriptionLock.ExitReadLock();
            }

            _msgsTotalCounter.SafeIncrement();
            _msgsPerSecCounter.SafeIncrement();
        }

        
        public virtual void Dispose()
        {
        }

        private Topic GetTopic(string key)
        {
            return _topics.GetOrAdd(key, _ => new Topic(DefaultMessageStoreSize));
        }

        private void RemoveEvent(Subscription subscription, string eventKey)
        {
            Topic topic;
            if (_topics.TryGetValue(eventKey, out topic))
            {
                topic.RemoveSubscription(subscription);
                subscription.RemoveEvent(eventKey);
            }
        }
    }
}
